namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// MCP plugin callbacks the agent pushes to the Bridge via SignalR.
/// Agent → Bridge direction: the agent's <c>mcp__{server}__{tool}</c> proxy tools
/// invoke these on the Bridge, which attaches auth on the host and calls the real
/// MCP server. Part of the composed <see cref="IAgentHubClient"/> surface — these
/// callbacks share the single SignalR hub connection and route by method name.
/// </summary>
public interface IMcpHubClient
{
    /// <summary>
    /// Agent → Bridge. Invoke a single MCP tool on the host and return its result.
    /// </summary>
    Task<McpToolResult> InvokeMcpTool(McpToolInvocation invocation);

    /// <summary>
    /// Agent → Bridge. Best-effort cancellation of an in-flight MCP tool invocation by its
    /// stable id. A no-op when the invocation already completed or is unknown.
    /// </summary>
    Task CancelMcpTool(McpToolCancellation cancellation);

    /// <summary>
    /// Agent → Bridge. Look up the current status of one approval-gated MCP action by its
    /// durable action id.
    /// </summary>
    Task<McpActionStatusResponse> GetMcpActionStatus(McpActionStatusRequest request);

    /// <summary>
    /// Agent → Bridge. Cancel one approval-gated MCP action, bound to its exact
    /// canonical-argument hash. Proposed/approved actions cancel immediately; a dispatching
    /// action only records the request and asks the active invocation to cancel — if the
    /// dispatch already reached the remote server the action resolves to
    /// <c>outcome_unknown</c>, never <c>cancelled</c>.
    /// </summary>
    Task<McpActionCancelResponse> CancelMcpAction(McpActionCancelRequest request);
}
