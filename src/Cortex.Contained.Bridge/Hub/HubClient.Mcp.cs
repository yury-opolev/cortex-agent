using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cortex.Contained.Bridge.Hub;

/// <summary>
/// MCP plugin-system extensions for <see cref="HubClient"/>:
///   - Outbound push of the namespaced tool catalog to the agent (<see cref="IAgentHub.UpdateMcpToolCatalog"/>).
///   - Inbound callback for agent-initiated tool invocations (<see cref="IAgentHubClient.InvokeMcpTool"/>),
///     surfaced as the <see cref="OnInvokeMcpTool"/> event which the host wires to the MCP host service.
///   - Inbound callback for agent-initiated cancellations (<see cref="IAgentHubClient.CancelMcpTool"/>).
/// Every invocation is registered with the <see cref="McpInvocationTracker"/> before it is
/// dispatched and completed in a <c>finally</c>; a <c>CancelMcpTool</c> cancels exactly the
/// matching invocation, and connection close/reconnect/replacement/dispose cancels all of them.
/// </summary>
public sealed partial class HubClient
{
    private readonly McpInvocationTracker mcpInvocationTracker = new();
    private int mcpConnectionGeneration;

    /// <summary>
    /// Agent → Bridge: invoke an MCP tool. The host attaches auth and calls the real server.
    /// The <see cref="CancellationToken"/> is the invocation's tracker token: it fires when the
    /// agent cancels this invocation or the hub connection is lost.
    /// </summary>
    public event Func<McpToolInvocation, CancellationToken, Task<McpToolResult>>? OnInvokeMcpTool;

    /// <summary>Bridge → Agent: replace the agent's MCP tool catalog with <paramref name="catalog"/>.</summary>
    public async Task PushMcpToolCatalogAsync(McpToolCatalog catalog, CancellationToken cancellationToken)
    {
        EnsureConnected();
        await this.connection!.InvokeAsync(nameof(IAgentHub.UpdateMcpToolCatalog), catalog, cancellationToken).ConfigureAwait(false);
    }

    private void RegisterMcpCallbacks(HubConnection connection)
    {
        // A new connection replaces the previous one (or is the first). Any invocation still
        // in flight from a previous connection can no longer deliver its result — the agent's
        // pending SignalR invoke died with that connection — so cancel it now. This runs
        // BEFORE the new connection starts receiving, so no new invocation can be hit.
        var generation = Interlocked.Increment(ref this.mcpConnectionGeneration);
        this.mcpInvocationTracker.CancelAll("hub connection replaced");

        connection.On<McpToolInvocation, McpToolResult>(
            nameof(IAgentHubClient.InvokeMcpTool),
            invocation => this.DispatchMcpInvocationAsync(invocation));

        connection.On<McpToolCancellation>(
            nameof(IAgentHubClient.CancelMcpTool),
            cancellation =>
            {
                var found = this.mcpInvocationTracker.Cancel(cancellation.InvocationId, cancellation.Reason);
                this.LogMcpCancellationReceived(cancellation.InvocationId, cancellation.Reason, found);
                return Task.CompletedTask;
            });

        // Close/reconnect of the CURRENT connection strands any in-flight invocation the same
        // way — its result can never reach the agent. The generation guard keeps a late Closed
        // event from a disposed, already-replaced connection from cancelling fresh invocations.
        connection.Closed += _ =>
        {
            if (generation == Volatile.Read(ref this.mcpConnectionGeneration))
            {
                this.mcpInvocationTracker.CancelAll("hub connection closed");
            }

            return Task.CompletedTask;
        };

        connection.Reconnected += _ =>
        {
            if (generation == Volatile.Read(ref this.mcpConnectionGeneration))
            {
                this.mcpInvocationTracker.CancelAll("hub connection re-established");
            }

            return Task.CompletedTask;
        };
    }

    private async Task<McpToolResult> DispatchMcpInvocationAsync(McpToolInvocation invocation)
    {
        var handler = this.OnInvokeMcpTool;
        if (handler is null)
        {
            return McpToolResult.Fail(
                invocation.InvocationId, McpFailureKind.Unavailable, "No MCP host handler registered on the Bridge.");
        }

        // At-most-once per id on this host: a duplicate of an ACTIVE invocation is rejected
        // before dispatch (definitive), never executed a second time.
        if (!this.mcpInvocationTracker.TryRegister(invocation.InvocationId, CancellationToken.None, out var token))
        {
            this.LogMcpDuplicateInvocation(invocation.InvocationId, invocation.ServerKey, invocation.ToolName);
            return McpToolResult.Fail(
                invocation.InvocationId,
                McpFailureKind.Validation,
                $"MCP invocation '{invocation.InvocationId}' is already in flight.");
        }

        try
        {
            return await handler(invocation, token).ConfigureAwait(false);
        }
        finally
        {
            this.mcpInvocationTracker.Complete(invocation.InvocationId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP cancellation received for invocation {InvocationId} (reason: {Reason}); matched in-flight invocation: {Found}")]
    private partial void LogMcpCancellationReceived(string invocationId, string? reason, bool found);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Duplicate MCP invocation {InvocationId} for {ServerKey}/{ToolName} rejected")]
    private partial void LogMcpDuplicateInvocation(string invocationId, string serverKey, string toolName);
}
