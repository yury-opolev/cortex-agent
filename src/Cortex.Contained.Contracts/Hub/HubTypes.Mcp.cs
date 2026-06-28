namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// A single MCP tool exposed to the agent, namespaced under its owning server.
/// Pushed from the Bridge (the MCP host) to the agent as part of an
/// <see cref="McpToolCatalog"/>.
/// </summary>
public sealed record McpToolDefinition
{
    /// <summary>The owning MCP server's key (e.g. <c>github</c>).</summary>
    public required string ServerKey { get; init; }

    /// <summary>The tool's name as reported by the MCP server (e.g. <c>create_issue</c>).</summary>
    public required string ToolName { get; init; }

    /// <summary>Namespaced agent-facing name: <c>mcp__{ServerKey}__{ToolName}</c>.</summary>
    public required string FullName { get; init; }

    /// <summary>Human-readable description for the LLM.</summary>
    public required string Description { get; init; }

    /// <summary>JSON Schema (string) for the tool's parameters.</summary>
    public required string ParametersSchemaJson { get; init; }
}

/// <summary>
/// Full, replace-all catalog of currently-available MCP tools across all enabled
/// servers. The Bridge re-pushes this whenever the available tool set changes.
/// </summary>
public sealed record McpToolCatalog
{
    /// <summary>The complete set of currently-available MCP tools.</summary>
    public IReadOnlyList<McpToolDefinition> Tools { get; init; } = [];
}

/// <summary>
/// An agent-initiated request to invoke a single MCP tool. Routed Agent → Bridge
/// over the hub; the Bridge attaches auth and calls the real MCP server.
/// </summary>
public sealed record McpToolInvocation
{
    /// <summary>The target MCP server's key.</summary>
    public required string ServerKey { get; init; }

    /// <summary>The tool's name as reported by the MCP server.</summary>
    public required string ToolName { get; init; }

    /// <summary>JSON-encoded tool arguments.</summary>
    public required string ArgumentsJson { get; init; }

    /// <summary>Originating conversation id, for tracing. Null when not in scope.</summary>
    public string? ConversationId { get; init; }
}

/// <summary>
/// The result of an MCP tool invocation, mapped back to the agent over the hub.
/// </summary>
public sealed record McpToolResult
{
    /// <summary>Whether the invocation failed (transport error, auth, or MCP tool error).</summary>
    public required bool IsError { get; init; }

    /// <summary>Flattened MCP content (text/json) on success; empty on error.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>True when the server needs authorization before it can be used.</summary>
    public bool NeedsAuth { get; init; }

    /// <summary>Error message when <see cref="IsError"/> is true; otherwise null.</summary>
    public string? Error { get; init; }

    /// <summary>A successful result carrying <paramref name="content"/>.</summary>
    public static McpToolResult Ok(string content) => new() { IsError = false, Content = content };

    /// <summary>A failed result carrying an <paramref name="error"/> message, optionally flagged as needing auth.</summary>
    public static McpToolResult Fail(string error, bool needsAuth = false) =>
        new() { IsError = true, Error = error, NeedsAuth = needsAuth };
}
