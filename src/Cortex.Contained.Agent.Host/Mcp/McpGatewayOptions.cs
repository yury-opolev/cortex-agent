namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Agent-Host-side options for the MCP gateway.
/// </summary>
public sealed class McpGatewayOptions
{
    /// <summary>
    /// Ceiling (seconds) for any single Agent→Bridge MCP tool invoke. Backstops an
    /// unresponsive Bridge so a stuck MCP call cannot hold the per-channel lock.
    /// Clamped by the gateway to 1–60 seconds; the 60-second hard ceiling can never be
    /// exceeded. When it fires the gateway sends a best-effort cancellation and reports
    /// the invocation's outcome as unknown. Default 60.
    /// </summary>
    public int BridgeInvokeTimeoutSeconds { get; set; } = 60;
}
