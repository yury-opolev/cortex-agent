using System.Text.Json;
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
    private readonly Lock stateLock = new();

    private McpClient? client;
    private IReadOnlyList<McpToolDefinition> tools = [];
    private McpServerStatus status = McpServerStatus.Disconnected;
    private string? lastError;

    protected McpServerConnectionBase(
        string serverKey,
        IReadOnlyCollection<string> toolAllowList,
        IReadOnlyCollection<string> mutationToolAllowList,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        this.ServerKey = serverKey;
        this.toolAllowList = toolAllowList ?? [];
        this.mutationToolAllowList = mutationToolAllowList ?? [];
        this.logger = logger;
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
            this.SetStatus(McpServerStatus.Error, ex.Message);
            this.LogConnectFailed(this.ServerKey, ex.Message);
        }
#pragma warning restore CA1031
    }

    public async Task<McpToolResult> CallToolAsync(McpToolInvocation invocation, CancellationToken cancellationToken)
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
        // through this direct path at all: it requires the human-approval flow, which binds the
        // approval to the exact canonical argument hash.
        if (McpToolFilter.IsMutation(toolName, this.mutationToolAllowList))
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
            // Log the detail host-side only; the agent receives a sanitized, secret-free message.
            this.LogToolFailed(this.ServerKey, toolName, ex.Message);
            return McpToolResult.Fail(
                invocationId, McpFailureKind.Validation, McpErrorSanitizer.ToolFailure(this.ServerKey, toolName));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // Cancelled before dispatch — definitive, the call never left the host.
            return McpToolResult.Cancelled(invocationId, $"MCP tool '{toolName}' invocation was cancelled before dispatch.");
        }

        try
        {
            var result = await activeClient.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var mapped = McpResultMapper.ToToolResult(invocationId, result);
            this.LogToolInvoked(this.ServerKey, toolName, mapped.IsError);
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
                this.LogTransportFailed(this.ServerKey, toolName, ex.Message);
                this.HandleTransportFailure($"MCP fault during '{toolName}': {ex.Message}");
            }
            else
            {
                this.LogToolFailed(this.ServerKey, toolName, ex.Message);
            }

            return mapped;
        }
#pragma warning disable CA1031 // Transport failures map to a structured result, never thrown.
        catch (Exception ex)
        {
            // Fatal transport closure after dispatch started (process exit, broken pipe, socket
            // loss): the outcome is unknowable, the connection is dead, and the ORIGINAL
            // invocation is never replayed. Clear client/tools so the catalog drops them.
            this.LogTransportFailed(this.ServerKey, toolName, ex.Message);
            this.HandleTransportFailure($"transport failed during '{toolName}': {ex.Message}");
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
            this.LogDisposeFailed(this.ServerKey, ex.Message);
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
                this.LogDisposeFailed(this.ServerKey, ex.Message);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP tool invoked: server '{ServerKey}' tool '{ToolName}' isError={IsError}")]
    private partial void LogToolInvoked(string serverKey, string toolName, bool isError);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool failed: server '{ServerKey}' tool '{ToolName}': {Error}")]
    private partial void LogToolFailed(string serverKey, string toolName, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool cancelled mid-call: server '{ServerKey}' tool '{ToolName}' invocation {InvocationId}; outcome unknown")]
    private partial void LogToolCancelledMidCall(string serverKey, string toolName, string invocationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP transport failed mid-call: server '{ServerKey}' tool '{ToolName}': {Error}; connection moved to Error")]
    private partial void LogTransportFailed(string serverKey, string toolName, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP tool invocation blocked by allow-list: server '{ServerKey}' tool '{ToolName}'")]
    private partial void LogToolNotAllowed(string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP mutation tool refused on direct path: server '{ServerKey}' tool '{ToolName}' requires approval")]
    private partial void LogMutationToolRefused(string serverKey, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP server '{ServerKey}' dispose failed: {Error}")]
    private partial void LogDisposeFailed(string serverKey, string error);
}
