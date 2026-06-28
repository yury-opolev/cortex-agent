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
    /// Invoke an MCP tool on the Bridge. Never throws for transport failures —
    /// they map to <see cref="McpToolResult.Fail(string, bool)"/>.
    /// </summary>
    Task<McpToolResult> InvokeAsync(
        string serverKey,
        string toolName,
        string argumentsJson,
        string? conversationId,
        CancellationToken cancellationToken);
}
