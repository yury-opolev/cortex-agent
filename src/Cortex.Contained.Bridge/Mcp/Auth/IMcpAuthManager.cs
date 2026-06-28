using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>Resolves the host-side authentication for an MCP server from its config + DPAPI secrets.</summary>
public interface IMcpAuthManager
{
    /// <summary>Resolves auth (env/headers or a needs-login signal) for <paramref name="server"/>.</summary>
    McpResolvedAuth Resolve(McpServerConfig server);
}
