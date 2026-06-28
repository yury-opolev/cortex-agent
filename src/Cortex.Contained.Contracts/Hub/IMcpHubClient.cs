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
}
