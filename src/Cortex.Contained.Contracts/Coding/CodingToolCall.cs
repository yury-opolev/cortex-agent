namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Compact summary of a single tool call performed by the coding agent (claude).
/// Stored in the session row's <c>last_tool_calls</c> JSON column (last 5 only).
/// </summary>
public sealed record CodingToolCall
{
    /// <summary>Tool name (e.g. <c>Read</c>, <c>Edit</c>, <c>Bash</c>).</summary>
    public required string Name { get; init; }

    /// <summary>One-line summary of the input. For <c>Bash</c>, the command. For <c>Edit</c>/<c>Read</c>, the path.</summary>
    public required string ArgsSummary { get; init; }

    /// <summary>Status (<c>started</c> | <c>completed</c> | <c>failed</c>).</summary>
    public required string Status { get; init; }

    /// <summary>UTC timestamp when the call was issued.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }
}
