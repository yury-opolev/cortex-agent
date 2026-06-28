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

    /// <summary>Invokes a tool by its server-local name. Errors are mapped to a structured <see cref="McpToolResult"/>, never thrown.</summary>
    Task<McpToolResult> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken);
}
