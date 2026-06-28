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
    private readonly SemaphoreSlim reconcileLock = new(1, 1);
    private readonly Lock stateLock = new();

    // key -> live connection; key -> config signature used to detect changes.
    private readonly Dictionary<string, IMcpServerConnection> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> signatures = new(StringComparer.Ordinal);

    public McpHostService(IMcpServerConnectionFactory factory, ILogger<McpHostService> logger)
    {
        this.factory = factory;
        this.logger = logger;
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

        await this.reconcileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desired = BuildDesired(settings);

            await this.StopUndesiredAsync(desired).ConfigureAwait(false);
            await this.StartDesiredAsync(desired, cancellationToken).ConfigureAwait(false);

            await this.RebuildCatalogAndNotifyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.reconcileLock.Release();
        }
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

    /// <summary>Routes a tool invocation to the owning connection, or returns a structured failure when it is unavailable.</summary>
    public async Task<McpToolResult> InvokeAsync(
        string serverKey, string toolName, string argumentsJson, CancellationToken cancellationToken)
    {
        IMcpServerConnection? connection;
        lock (this.stateLock)
        {
            this.connections.TryGetValue(serverKey, out connection);
        }

        if (connection is null)
        {
            this.LogInvokeUnavailable(serverKey, toolName);
            return McpToolResult.Fail($"MCP server '{serverKey}' is not available.");
        }

        return await connection.CallToolAsync(toolName, argumentsJson, cancellationToken).ConfigureAwait(false);
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
            bool alreadyRunning;
            lock (this.stateLock)
            {
                alreadyRunning = this.connections.ContainsKey(key);
            }

            if (alreadyRunning)
            {
                continue;
            }

            var connection = this.factory.TryCreate(server);
            if (connection is null)
            {
                continue;
            }

            this.LogStarting(key);
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);

            lock (this.stateLock)
            {
                this.connections[key] = connection;
                this.signatures[key] = Signature(server);
            }
        }
    }

    private async Task RebuildCatalogAndNotifyAsync(CancellationToken cancellationToken)
    {
        List<McpToolDefinition> tools;
        lock (this.stateLock)
        {
            tools = this.connections.Values.SelectMany(c => c.Tools).ToList();
        }

        var catalog = new McpToolCatalog { Tools = tools };
        this.CurrentCatalog = catalog;
        this.LogCatalogRebuilt(tools.Count);

        var handler = this.CatalogChanged;
        if (handler is not null)
        {
            await handler.Invoke(catalog, cancellationToken).ConfigureAwait(false);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP catalog rebuilt: {ToolCount} tools")]
    private partial void LogCatalogRebuilt(int toolCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP invoke for unavailable server '{ServerKey}' tool '{ToolName}'")]
    private partial void LogInvokeUnavailable(string serverKey, string toolName);
}
