namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Agent-Host-side options for the MCP gateway.
/// </summary>
public sealed class McpGatewayOptions
{
    /// <summary>
    /// Ceiling (seconds) for any single Agent→Bridge MCP tool invoke. Backstops an
    /// unresponsive Bridge so a stuck MCP call cannot hold the per-channel lock. Default 60.
    /// </summary>
    public int BridgeInvokeTimeoutSeconds { get; set; } = 60;
}
