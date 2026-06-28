using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Builds an <see cref="IMcpServerConnection"/> for a configured server, resolving auth on the host.
/// Returns null when the server cannot be connected non-interactively (missing transport fields, or
/// an auth mode that needs an interactive login). The seam that lets <see cref="McpHostService"/> be
/// unit-tested with fake connections.
/// </summary>
public interface IMcpServerConnectionFactory
{
    /// <summary>Creates a connection for <paramref name="server"/>, or null when it cannot be auto-connected.</summary>
    IMcpServerConnection? TryCreate(McpServerConfig server);
}
