using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Owns the set of live MCP server connections. Reconciles them against config + enable flags
/// (start-if-should / stop-if-disabled, mirroring the sidecar lifecycles), aggregates the
/// namespaced tool catalog, and routes <c>tools/call</c> invocations to the owning connection.
/// Raises <see cref="CatalogChanged"/> whenever the aggregated catalog changes so the pusher can
/// re-push it to the agent.
/// </summary>
public sealed partial class McpHostService : IAsyncDisposable
{
    private readonly IMcpServerConnectionFactory factory;
    private readonly ILogger<McpHostService> logger;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim reconcileLock = new(1, 1);
    private readonly Lock stateLock = new();

    // key -> live connection; key -> config signature used to detect changes;
    // key -> backoff state for a connection that failed to connect (so a permanently-broken
    // server is retried on later reconciles, but with exponential backoff — never a hot loop).
    private readonly Dictionary<string, IMcpServerConnection> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> signatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetryState> retries = new(StringComparer.Ordinal);
    private string? lastPushedSignature;

    public McpHostService(IMcpServerConnectionFactory factory, ILogger<McpHostService> logger)
        : this(factory, logger, timeProvider: null)
    {
    }

    public McpHostService(IMcpServerConnectionFactory factory, ILogger<McpHostService> logger, TimeProvider? timeProvider)
    {
        this.factory = factory;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after a reconcile changes the aggregated catalog. Awaited by the pusher.</summary>
    public event Func<McpToolCatalog, CancellationToken, Task>? CatalogChanged;

    /// <summary>The most recently aggregated catalog.</summary>
    public McpToolCatalog CurrentCatalog { get; private set; } = new();

    /// <summary>
    /// Reconciles live connections against <paramref name="settings"/>: stops servers that are
    /// removed/disabled/changed (or all of them when the master switch is off) and starts servers
    /// that should be running. Then rebuilds the catalog and raises <see cref="CatalogChanged"/>.
    /// </summary>
    public async Task ReconcileAsync(McpSettingsConfig settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        McpToolCatalog catalog;
        await this.reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desired = BuildDesired(settings);

            await this.StopUndesiredAsync(desired).ConfigureAwait(false);
            await this.StartDesiredAsync(desired, cancellationToken).ConfigureAwait(false);

            catalog = this.RebuildCatalog();
        }
        finally
        {
            this.reconcileLock.Release();
        }

        // M4: fire the catalog push OUTSIDE the reconcile lock so a slow tenant's SignalR I/O can't
        // serialize every reconcile. The snapshot above is immutable, so the push sees a stable view.
        await this.NotifyCatalogChangedAsync(catalog, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forces a disconnect + reconnect of a single server, picking up a rotated secret (the factory
    /// re-resolves auth on rebuild). Clears any backoff so the reconnect is immediate. Then reconciles
    /// against <paramref name="settings"/> to recreate the connection and re-push the catalog.
    /// </summary>
    public async Task ForceReconnectAsync(string serverKey, McpSettingsConfig settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentNullException.ThrowIfNull(settings);

        var key = serverKey.ToLowerInvariant();
        IMcpServerConnection? toDispose;
        lock (this.stateLock)
        {
            this.connections.Remove(key, out toDispose);
            this.signatures.Remove(key);
            this.retries.Remove(key);
        }

        if (toDispose is not null)
        {
            this.LogForceReconnect(key);
            await toDispose.DisposeAsync().ConfigureAwait(false);
        }

        await this.ReconcileAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a point-in-time runtime snapshot (status, last error, tools) for the live connection of
    /// <paramref name="serverKey"/>, or <c>null</c> when no connection exists (disabled, never started,
    /// or skipped because it needs interactive login). Carries no secret material.
    /// </summary>
    public McpServerRuntimeInfo? GetServerInfo(string serverKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);

        lock (this.stateLock)
        {
            if (this.connections.TryGetValue(serverKey.ToLowerInvariant(), out var connection))
            {
                return new McpServerRuntimeInfo
                {
                    Status = connection.Status,
                    LastError = connection.LastError,
                    Tools = connection.Tools,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Routes a tool invocation to the owning connection, preserving its identity/correlation end
    /// to end, or returns a definitive structured failure when the server is unavailable before
    /// dispatch. If the connection's transport died during the call, the catalog is rebuilt and
    /// re-pushed immediately so the dead tools disappear; the ORIGINAL invocation is never
    /// replayed — the periodic reconciliation only reconnects the server for FUTURE calls.
    /// </summary>
    public async Task<McpToolResult> InvokeAsync(McpToolInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        IMcpServerConnection? connection;
        lock (this.stateLock)
        {
            this.connections.TryGetValue(invocation.ServerKey, out connection);
        }

        if (connection is null)
        {
            this.LogInvokeUnavailable(invocation.ServerKey, invocation.ToolName);
            return McpToolResult.Fail(
                invocation.InvocationId,
                McpFailureKind.Unavailable,
                $"MCP server '{invocation.ServerKey}' is not available.");
        }

        var result = await connection.CallToolAsync(invocation, cancellationToken).ConfigureAwait(false);

        if (connection.Status == McpServerStatus.Error)
        {
            // Fatal transport closure mid-call: drop the dead server's tools from the agent's
            // catalog now (deliberately not bound to the possibly-cancelled invocation token).
            var catalog = this.RebuildCatalog();
            await this.NotifyCatalogChangedAsync(catalog, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    private static Dictionary<string, McpServerConfig> BuildDesired(McpSettingsConfig settings)
    {
        var desired = new Dictionary<string, McpServerConfig>(StringComparer.Ordinal);
        if (!settings.Enabled)
        {
            return desired;
        }

        foreach (var server in settings.Servers)
        {
            if (!server.Enabled || string.IsNullOrWhiteSpace(server.Key))
            {
                continue;
            }

            desired[server.Key.ToLowerInvariant()] = server;
        }

        return desired;
    }

    private async Task StopUndesiredAsync(Dictionary<string, McpServerConfig> desired)
    {
        List<string> toStop;
        lock (this.stateLock)
        {
            toStop = this.connections.Keys
                .Where(key => !desired.TryGetValue(key, out var cfg)
                    || !string.Equals(this.signatures.GetValueOrDefault(key), Signature(cfg), StringComparison.Ordinal))
                .ToList();
        }

        foreach (var key in toStop)
        {
            IMcpServerConnection? connection;
            lock (this.stateLock)
            {
                this.connections.Remove(key, out connection);
                this.signatures.Remove(key);
                this.retries.Remove(key);
            }

            if (connection is not null)
            {
                this.LogStopping(key);
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task StartDesiredAsync(
        Dictionary<string, McpServerConfig> desired, CancellationToken cancellationToken)
    {
        foreach (var (key, server) in desired)
        {
            // M1: a connection that is present but not Connected (failed handshake / dropped) counts
            // as "not running" — dispose + recreate it, but only once its backoff window has elapsed.
            IMcpServerConnection? existing;
            bool backingOff;
            lock (this.stateLock)
            {
                this.connections.TryGetValue(key, out existing);
                backingOff = existing is not null
                    && !IsHealthy(existing.Status)
                    && this.retries.TryGetValue(key, out var state)
                    && this.timeProvider.GetUtcNow() < state.NextAttemptAt;
            }

            if (existing is not null && IsHealthy(existing.Status))
            {
                continue;
            }

            if (backingOff)
            {
                continue;
            }

            if (existing is not null)
            {
                lock (this.stateLock)
                {
                    this.connections.Remove(key);
                }

                this.LogRetrying(key);
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var connection = this.factory.TryCreate(server);
            if (connection is null)
            {
                continue;
            }

            this.LogStarting(key);
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var connected = IsHealthy(connection.Status);
            bool recovered;
            lock (this.stateLock)
            {
                this.connections[key] = connection;
                this.signatures[key] = Signature(server);
                if (connected)
                {
                    recovered = this.retries.Remove(key);
                }
                else
                {
                    var attempts = (this.retries.TryGetValue(key, out var prev) ? prev.Attempts : 0) + 1;
                    this.retries[key] = new RetryState
                    {
                        Attempts = attempts,
                        NextAttemptAt = this.timeProvider.GetUtcNow() + McpReconnectBackoff.DelayFor(attempts),
                    };
                    recovered = false;
                }
            }

            if (recovered)
            {
                this.LogServerRecovered(key);
            }
        }
    }

    private McpToolCatalog RebuildCatalog()
    {
        List<McpToolDefinition> tools;
        lock (this.stateLock)
        {
            tools = this.connections.Values.SelectMany(c => c.Tools).ToList();
        }

        var catalog = new McpToolCatalog { Tools = tools };
        this.CurrentCatalog = catalog;
        this.LogCatalogRebuilt(tools.Count);
        return catalog;
    }

    private async Task NotifyCatalogChangedAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
    {
        var handler = this.CatalogChanged;
        if (handler is null)
        {
            return;
        }

        // Only push when the aggregated catalog actually changed. The periodic reconcile (which
        // drives auto-retry of failed servers) would otherwise re-push an identical catalog to every
        // tenant on each idle tick.
        var signature = CatalogSignature(catalog);
        lock (this.stateLock)
        {
            if (string.Equals(signature, this.lastPushedSignature, StringComparison.Ordinal))
            {
                return;
            }

            this.lastPushedSignature = signature;
        }

        await handler.Invoke(catalog, cancellationToken).ConfigureAwait(false);
    }

    private static string CatalogSignature(McpToolCatalog catalog)
    {
        return string.Join(
            '\n',
            catalog.Tools
                .OrderBy(t => t.FullName, StringComparer.Ordinal)
                .Select(t => $"{t.FullName}{t.Description}{t.ParametersSchemaJson}"));
    }

    private static bool IsHealthy(McpServerStatus status) => status == McpServerStatus.Connected;

    private static string Signature(McpServerConfig server)
    {
        var env = string.Join(',', server.Env.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => $"{kvp.Key}={kvp.Value}"));
        var args = string.Join(',', server.Args);
        var allow = string.Join(',', server.ToolAllowList);
        return $"{server.Transport}|{server.Url}|{server.Command}|{args}|{env}|{server.Auth}|{server.ApiKeyHeader}|{server.SecretRef}|{allow}";
    }

    public async ValueTask DisposeAsync()
    {
        List<IMcpServerConnection> toDispose;
        lock (this.stateLock)
        {
            toDispose = this.connections.Values.ToList();
            this.connections.Clear();
            this.signatures.Clear();
            this.retries.Clear();
        }

        foreach (var connection in toDispose)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        this.reconcileLock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host starting server '{ServerKey}'")]
    private partial void LogStarting(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host stopping server '{ServerKey}'")]
    private partial void LogStopping(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host retrying failed server '{ServerKey}' (backoff elapsed)")]
    private partial void LogRetrying(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host server '{ServerKey}' recovered after earlier failure")]
    private partial void LogServerRecovered(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP host force-reconnecting server '{ServerKey}'")]
    private partial void LogForceReconnect(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP catalog rebuilt: {ToolCount} tools")]
    private partial void LogCatalogRebuilt(int toolCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke for unavailable server '{ServerKey}' tool '{ToolName}'")]
    private partial void LogInvokeUnavailable(string serverKey, string toolName);

    /// <summary>Backoff bookkeeping for a server whose connection failed and is awaiting retry.</summary>
    private sealed class RetryState
    {
        public required int Attempts { get; init; }

        public required DateTimeOffset NextAttemptAt { get; init; }
    }
}
