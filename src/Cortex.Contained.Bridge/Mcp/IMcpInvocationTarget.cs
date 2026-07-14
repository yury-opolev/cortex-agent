using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Host-side seam for dispatching MCP tool invocations to their owning server connection.
/// Implemented by <see cref="McpHostService"/>; consumed by the approval service (direct
/// read-tool path) and the outbox dispatcher (approved-mutation path) so both are unit-testable.
/// </summary>
public interface IMcpInvocationTarget
{
    /// <summary>
    /// Direct invocation path. Enforces the allow-list AND refuses mutation-classified tools —
    /// a mutation must go through the durable approval flow instead.
    /// </summary>
    Task<McpToolResult> InvokeAsync(McpToolInvocation invocation, CancellationToken cancellationToken);

    /// <summary>
    /// Outbox-only dispatch path for a HUMAN-APPROVED mutation. Still enforces the ordinary
    /// allow-list, but bypasses the direct-path mutation refusal. Only the outbox dispatcher
    /// may call this, and only with the stored canonical arguments of an approved action.
    /// </summary>
    Task<McpToolResult> InvokeApprovedAsync(McpToolInvocation invocation, CancellationToken cancellationToken);
}
