namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// MCP plugin methods exposed by the agent (inside the Docker container).
/// Bridge → Agent direction: the Bridge (the MCP host) pushes the namespaced
/// tool catalog so the agent can register <c>mcp__{server}__{tool}</c> proxy tools.
/// Part of the composed <see cref="IAgentHub"/> surface — these methods share the
/// single SignalR hub connection and route by method name.
/// </summary>
public interface IMcpHub
{
    /// <summary>
    /// Bridge → Agent. Replace-all push of the currently-available MCP tool catalog.
    /// The agent rebuilds its MCP proxy tool set from the supplied definitions.
    /// </summary>
    Task UpdateMcpToolCatalog(McpToolCatalog catalog);
}
