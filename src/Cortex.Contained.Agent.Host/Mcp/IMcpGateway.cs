using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Mcp;

/// <summary>
/// Agent-side proxy to the Bridge's MCP host. Given a target server/tool and
/// JSON arguments, routes the invocation to the Bridge (which attaches auth on the
/// host and calls the real MCP server) and returns the result.
/// </summary>
public interface IMcpGateway
{
    /// <summary>
    /// Invoke an MCP tool on the Bridge. Generates the invocation's stable id and never throws
    /// for transport failures — pre-dispatch failures map to a definitive
    /// <see cref="McpToolOutcome.Failed"/>, while post-dispatch timeouts, cancellations, and
    /// transport losses map to <see cref="McpToolOutcome.OutcomeUnknown"/> (never auto-retried).
    /// </summary>
    Task<McpToolResult> InvokeAsync(
        string serverKey,
        string toolName,
        string argumentsJson,
        string? conversationId,
        string? channelId,
        string? correlationId,
        CancellationToken cancellationToken);
}
