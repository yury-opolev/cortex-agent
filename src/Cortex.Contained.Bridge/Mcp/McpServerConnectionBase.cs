using System.Diagnostics;
using System.Text.Json;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Shared SDK-client lifecycle for <see cref="IMcpServerConnection"/> implementations. Subclasses
/// supply only the transport (<see cref="CreateTransport"/>); this base owns connect → list →
/// filter → cache, the <c>tools/call</c> path, status transitions, and disposal. Deliberately
/// thin — the per-transport surface is just the transport factory.
/// </summary>
public abstract partial class McpServerConnectionBase : IMcpServerConnection
{
    private readonly IReadOnlyCollection<string> toolAllowList;
    private readonly IReadOnlyCollection<string> mutationToolAllowList;
    private readonly ILogger logger;
    private readonly int callTimeoutSeconds;
    private readonly int maxResultBytes;
    private readonly Lock stateLock = new();

    private McpClient? client;
    private IReadOnlyList<McpToolDefinition> tools = [];
    private McpServerStatus status = McpServerStatus.Disconnected;
    private string? lastError;

    protected McpServerConnectionBase(
        string serverKey,
        IReadOnlyCollection<string> toolAllowList,
        IReadOnlyCollection<string> mutationToolAllowList,
        ILogger logger,
        int callTimeoutSeconds = McpServerConfig.DefaultCallTimeoutSeconds,
        int maxResultBytes = McpResultMapper.DefaultMaxResultBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        // SECURITY: mutationToolAllowList is a safety-critical policy input — a null would fail
        // OPEN (classify nothing as a mutation, letting a state-changing tool through the direct
        // path). It is a REQUIRED, non-null parameter; the factory always supplies it.
        ArgumentNullException.ThrowIfNull(mutationToolAllowList);
        this.ServerKey = serverKey;
        this.toolAllowList = toolAllowList ?? [];
        this.mutationToolAllowList = mutationToolAllowList;
        this.logger = logger;
        // Guard the configured bound before it can reach CancelAfter: a hand-edited YAML value that
        // bypasses the admin-API validation (e.g. a negative or absurd timeout) would otherwise
        // throw ArgumentOutOfRangeException from CancelAfter mid-call. Clamp to the same sane range
        // the config boundary enforces so an out-of-range value degrades to a bounded call, not a crash.
        this.callTimeoutSeconds = Math.Clamp(
            callTimeoutSeconds, McpServerConfig.MinCallTimeoutSeconds, McpServerConfig.MaxCallTimeoutSeconds);
        this.maxResultBytes = maxResultBytes;
    }

    public string ServerKey { get; }

    public McpServerStatus Status
    {
        get
        {
            lock (this.stateLock)
            {
                return this.status;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (this.stateLock)
            {
                return this.lastError;
            }
        }
    }

    public IReadOnlyList<McpToolDefinition> Tools
    {
        get
        {
            lock (this.stateLock)
            {
                return this.tools;
            }
        }
    }

    /// <summary>Builds the transport for this connection. Called once per <see cref="ConnectAsync"/>.</summary>
    protected abstract IClientTransport CreateTransport();

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        this.SetStatus(McpServerStatus.Connecting, error: null);
        this.LogConnecting(this.ServerKey);

        try
        {
            var transport = this.CreateTransport();
            var created = await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: null, cancellationToken)
                .ConfigureAwait(false);

            var listed = await created.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var definitions = new List<McpToolDefinition>(listed.Count);
            foreach (var tool in listed)
            {
                if (!McpToolFilter.IsAllowed(tool.Name, this.toolAllowList))
                {
                    continue;
                }

                definitions.Add(new McpToolDefinition
                {
                    ServerKey = this.ServerKey,
                    ToolName = tool.Name,
                    FullName = McpToolNamer.Full(this.ServerKey, tool.Name),
                    Description = tool.Description ?? string.Empty,
                    ParametersSchemaJson = tool.JsonSchema.GetRawText(),
                    // Explicit admin classification only — never the server's own annotations.
                    RequiresApproval = McpToolFilter.IsMutation(tool.Name, this.mutationToolAllowList),
                });
            }

            lock (this.stateLock)
            {
                this.client = created;
                this.tools = definitions;
                this.status = McpServerStatus.Connected;
                this.lastError = null;
            }

            this.LogConnected(this.ServerKey, definitions.Count);
        }
#pragma warning disable CA1031 // Connection failures must surface via status, never crash the host.
        catch (Exception ex)
        {
            // SECURITY: a connect failure (e.g. a misconfigured HTTP/stdio endpoint) can embed a
            // connection-string secret in ex.Message. LastError is admin-facing
            // (McpServerView.LastError) and the log is host-side — both carry only the exception
            // TYPE via McpErrorSanitizer.ConnectFailure, never the raw message.
            this.SetStatus(McpServerStatus.Error, McpErrorSanitizer.ConnectFailure(this.ServerKey, ex));
            this.LogConnectFailed(this.ServerKey, ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    public Task<McpToolResult> CallToolAsync(McpToolInvocation invocation, CancellationToken cancellationToken)
        => this.CallToolCoreAsync(invocation, bypassMutationRefusal: false, cancellationToken);

    public Task<McpToolResult> CallApprovedMutationAsync(McpToolInvocation invocation, CancellationToken cancellationToken)
        => this.CallToolCoreAsync(invocation, bypassMutationRefusal: true, cancellationToken);

    private async Task<McpToolResult> CallToolCoreAsync(McpToolInvocation invocation, bool bypassMutationRefusal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var invocationId = invocation.InvocationId;
        var toolName = invocation.ToolName;

        // SECURITY: the allow-list is a policy boundary, not just a catalog filter. Re-check at
        // invoke time so a (prompt-injected) agent cannot call an excluded tool by naming it directly.
        if (!McpToolFilter.IsAllowed(toolName, this.toolAllowList))
        {
            this.LogToolNotAllowed(this.ServerKey, toolName);
            return McpToolResult.Fail(
                invocationId, McpFailureKind.Policy, $"MCP tool '{toolName}' is not permitted for server '{this.ServerKey}'.");
        }

        // SECURITY: mutation classification is RE-CHECKED immediately before dispatch — never
        // trusted from the catalog the agent saw. A mutation-classified tool must not execute
        // through the direct path at all: it requires the human-approval flow, which binds the
        // approval to the exact canonical argument hash. The ONLY caller allowed to bypass this
        // refusal is the outbox dispatcher (CallApprovedMutationAsync), which dispatches the
        // stored canonical arguments of a human-approved action.
        if (!bypassMutationRefusal && McpToolFilter.IsMutation(toolName, this.mutationToolAllowList))
        {
            this.LogMutationToolRefused(this.ServerKey, toolName);
            return McpToolResult.Fail(
                invocationId,
                McpFailureKind.Policy,
                $"MCP tool '{toolName}' on server '{this.ServerKey}' is classified as a mutation and requires human approval; it cannot be invoked via the direct path.");
        }

        McpClient? activeClient;
        lock (this.stateLock)
        {
            activeClient = this.status == McpServerStatus.Connected ? this.client : null;
        }

        if (activeClient is null)
        {
            return McpToolResult.Fail(
                invocationId, McpFailureKind.Unavailable, $"MCP server '{this.ServerKey}' is not connected.");
        }

        // Parse the arguments BEFORE dispatch: a malformed request is a definitive validation
        // failure — nothing reached the server, and the connection stays healthy.
        IReadOnlyDictionary<string, object?> arguments;
        try
        {
            arguments = McpArguments.Parse(invocation.ArgumentsJson);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Host-side log carries ONLY the exception type — never the raw message (it may embed
            // fragments of the untrusted arguments). The agent receives a separate, sanitized,
            // secret-free message via McpErrorSanitizer.
            this.LogToolFailed(this.ServerKey, toolName, invocationId, McpFailureKind.Validation, ex.GetType().Name);
            return McpToolResult.Fail(
                invocationId, McpFailureKind.Validation, McpErrorSanitizer.ToolFailure(this.ServerKey, toolName));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelled before dispatch — definitive, the call never left the host.
            return McpToolResult.Cancelled(invocationId, $"MCP tool '{toolName}' invocation was cancelled before dispatch.");
        }

        // BOUND every call to McpServerConfig.CallTimeoutSeconds: a hung or slow MCP server must
        // never block the invocation indefinitely. Linked to the caller's token so both the
        // caller's own cancellation AND this connection's configured ceiling can fire the same way;
        // the two are distinguished below so a configured-timeout firing is reported distinctly
        // from a caller cancellation (both are equally ambiguous outcomes, never auto-retried).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(this.callTimeoutSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await activeClient.CallToolAsync(toolName, arguments, cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);
            var mapped = McpResultMapper.ToToolResult(invocationId, result, this.maxResultBytes);
            this.LogToolInvoked(this.ServerKey, toolName, invocationId, mapped.Outcome, stopwatch.ElapsedMilliseconds);
            return mapped;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled after dispatch started: the server may have executed the call. The
            // connection itself is still healthy — only this invocation is ambiguous.
            this.LogToolCancelledMidCall(this.ServerKey, toolName, invocationId);
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Cancellation,
                $"MCP tool '{toolName}' on server '{this.ServerKey}' was cancelled mid-call; it may still have executed.");
        }
        catch (OperationCanceledException)
        {
            // The CONFIGURED call timeout elapsed (not caller cancellation): equally ambiguous —
            // the request already left the Bridge and may still be executing. Never auto-retried.
            // The connection itself is still healthy; only this invocation is bounded away.
            this.LogToolTimedOut(this.ServerKey, toolName, invocationId, this.callTimeoutSeconds);
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Timeout,
                $"MCP tool '{toolName}' on server '{this.ServerKey}' timed out after {this.callTimeoutSeconds}s; it may still have executed.");
        }
        catch (McpException ex)
        {
            // Post-dispatch MCP fault. McpResultMapper.FromCallException decides definiteness:
            //   * A McpProtocolException (server returned a JSON-RPC error RESPONSE) rejected the
            //     call at the protocol layer BEFORE the tool ran — the side effect definitively did
            //     NOT occur — so it is a definitive Failed/Tool and the connection stays healthy.
            //   * ANY other McpException (session terminated, transport-level, invalid response) is
            //     AMBIGUOUS: the request left the Bridge and MAY have executed. It becomes
            //     OutcomeUnknown (never auto-retried), and the now-suspect connection is torn down
            //     so its dead tools drop from the catalog and reconciliation rebuilds a fresh one.
            var mapped = McpResultMapper.FromCallException(invocationId, this.ServerKey, toolName, ex);
            if (mapped.Outcome == McpToolOutcome.OutcomeUnknown)
            {
                this.LogTransportFailed(this.ServerKey, toolName, invocationId, ex.GetType().Name);
                // SECURITY: HandleTransportFailure's reason lands in the admin-facing LastError
                // field (McpServerView.LastError) — sanitize it the same way the log line above
                // and the agent-facing error already are: exception TYPE only, never ex.Message.
                this.HandleTransportFailure(McpErrorSanitizer.TransportFailure(this.ServerKey, toolName, ex));
            }
            else
            {
                this.LogToolFailed(this.ServerKey, toolName, invocationId, mapped.FailureKind, ex.GetType().Name);
            }

            return mapped;
        }
#pragma warning disable CA1031 // Transport failures map to a structured result, never thrown.
        catch (Exception ex)
        {
            // Fatal transport closure after dispatch started (process exit, broken pipe, socket
            // loss): the outcome is unknowable, the connection is dead, and the ORIGINAL
            // invocation is never replayed. Clear client/tools so the catalog drops them.
            this.LogTransportFailed(this.ServerKey, toolName, invocationId, ex.GetType().Name);
            this.HandleTransportFailure(McpErrorSanitizer.TransportFailure(this.ServerKey, toolName, ex));
            return McpToolResult.Unknown(
                invocationId,
                McpFailureKind.Transport,
                $"MCP server '{this.ServerKey}' transport failed mid-call; the invocation may still have executed.");
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Tears down a connection whose transport failed mid-call: clears the client and cached
    /// tools and moves to <see cref="McpServerStatus.Error"/>. The host service drops the dead
    /// tools from the catalog immediately; the periodic reconciliation recreates the connection.
    /// </summary>
    /// <param name="reason">
    /// SECURITY: stored verbatim into <see cref="LastError"/>, which is admin-facing (surfaced via
    /// <see cref="McpServerView.LastError"/>). Callers MUST pass an already-sanitized, content-free
    /// reason (see <see cref="McpErrorSanitizer.TransportFailure"/>) — never a raw <c>ex.Message</c>.
    /// </param>
    private void HandleTransportFailure(string reason)
    {
        McpClient? deadClient;
        lock (this.stateLock)
        {
            deadClient = this.client;
            this.client = null;
            this.tools = [];
            this.status = McpServerStatus.Error;
            this.lastError = reason;
        }

        if (deadClient is not null)
        {
            // Best-effort, fire-and-forget: the dead client must not block the invocation's
            // (already ambiguous) result from reaching the agent.
            _ = this.DisposeClientQuietlyAsync(deadClient);
        }
    }

    private async Task DisposeClientQuietlyAsync(McpClient deadClient)
    {
        try
        {
            await deadClient.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort disposal of an already-dead client.
        catch (Exception ex)
        {
            // SECURITY: content-free — only the exception TYPE (a dispose failure's message could
            // echo a fragment of the underlying transport, e.g. a broken pipe path).
            this.LogDisposeFailed(this.ServerKey, ex.GetType().Name);
        }
#pragma warning restore CA1031
    }

    public async ValueTask DisposeAsync()
    {
        McpClient? toDispose;
        lock (this.stateLock)
        {
            toDispose = this.client;
            this.client = null;
            this.tools = [];
            this.status = McpServerStatus.Disconnected;
        }

        if (toDispose is not null)
        {
            try
            {
                await toDispose.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort disposal.
            catch (Exception ex)
            {
                // SECURITY: content-free — only the exception TYPE (see DisposeClientQuietlyAsync).
                this.LogDisposeFailed(this.ServerKey, ex.GetType().Name);
            }
#pragma warning restore CA1031
        }

        GC.SuppressFinalize(this);
    }

    private void SetStatus(McpServerStatus newStatus, string? error)
    {
        lock (this.stateLock)
        {
            this.status = newStatus;
            this.lastError = error;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}' connecting")]
    private partial void LogConnecting(string serverKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP server '{ServerKey}' connected: {ToolCount} tools")]
    private partial void LogConnected(string serverKey, int toolCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' connect failed: {Error}")]
    private partial void LogConnectFailed(string serverKey, string error);

    // SECURITY: every log below carries ONLY invocation id / server / tool / outcome / failure
    // kind / duration / exception TYPE (never a raw exception message, argument, or result — those
    // may originate from, or embed fragments of, an untrusted MCP process). See
    // McpTelemetrySanitizer on the Agent side for the matching rule applied to ToolExecutionMessage
    // and the persisted tool-call summary.
    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool invoked: server '{ServerKey}' tool '{ToolName}' invocation {InvocationId} outcome={Outcome} durationMs={DurationMs}")]
    private partial void LogToolInvoked(string serverKey, string toolName, string invocationId, McpToolOutcome outcome, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool failed: server '{ServerKey}' tool '{ToolName}' invocation {InvocationId} failureKind={FailureKind} exceptionType={ExceptionType}")]
    private partial void LogToolFailed(string serverKey, string toolName, string invocationId, McpFailureKind failureKind, string? exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool cancelled mid-call: server '{ServerKey}' tool '{ToolName}' invocation {InvocationId}; outcome unknown")]
    private partial void LogToolCancelledMidCall(string serverKey, string toolName, string invocationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool timed out: server '{ServerKey}' tool '{ToolName}' invocation {InvocationId} after {TimeoutSeconds}s; outcome unknown")]
    private partial void LogToolTimedOut(string serverKey, string toolName, string invocationId, int timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP transport failed mid-call: server '{ServerKey}' tool '{ToolName}' invocation {InvocationId} exceptionType={ExceptionType}; connection moved to Error")]
    private partial void LogTransportFailed(string serverKey, string toolName, string invocationId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool invocation blocked by allow-list: server '{ServerKey}' tool '{ToolName}'")]
    private partial void LogToolNotAllowed(string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP mutation tool refused on direct path: server '{ServerKey}' tool '{ToolName}' requires approval")]
    private partial void LogMutationToolRefused(string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' dispose failed: {Error}")]
    private partial void LogDisposeFailed(string serverKey, string error);
}
