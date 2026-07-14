using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// One live connection to one MCP server (stdio or http). Owns the SDK client, caches the
/// discovered (allow-list-filtered) tool catalog, and routes <c>tools/call</c>. All members
/// are safe to call concurrently.
/// </summary>
public interface IMcpServerConnection : IAsyncDisposable
{
    /// <summary>The owning server's key (used in the tool prefix and telemetry).</summary>
    string ServerKey { get; }

    /// <summary>Current connection status.</summary>
    McpServerStatus Status { get; }

    /// <summary>The most recent connect/list error, or null.</summary>
    string? LastError { get; }

    /// <summary>The discovered, allow-list-filtered, namespaced tools currently exposed by this server.</summary>
    IReadOnlyList<McpToolDefinition> Tools { get; }

    /// <summary>Connects, handshakes, and lists tools. Never throws — failures surface via <see cref="Status"/>.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Invokes the tool named by <paramref name="invocation"/>, preserving its identity/correlation
    /// end to end. Errors are mapped to a structured <see cref="McpToolResult"/> (definitive
    /// failure vs. ambiguous <see cref="McpToolOutcome.OutcomeUnknown"/>), never thrown. The
    /// invocation is dispatched at most once — never replayed.
    /// </summary>
    Task<McpToolResult> CallToolAsync(McpToolInvocation invocation, CancellationToken cancellationToken);
}
