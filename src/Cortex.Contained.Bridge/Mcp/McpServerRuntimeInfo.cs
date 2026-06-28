using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// A point-in-time snapshot of one live MCP server connection's runtime state, surfaced by
/// <see cref="McpHostService"/> for the Web UI / REST projection. Carries no secret material.
/// </summary>
public sealed record McpServerRuntimeInfo
{
    /// <summary>Current connection status.</summary>
    public required McpServerStatus Status { get; init; }

    /// <summary>The most recent connect/list error, or null.</summary>
    public string? LastError { get; init; }

    /// <summary>The discovered, allow-list-filtered tools currently exposed by this server.</summary>
    public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];
}
