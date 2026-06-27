namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// A single message in a coding session's transcript, as returned by the
/// <c>coding_session_history</c> tool. Mirrors coda serve's <c>{ role, content }</c> shape.
/// </summary>
public sealed record CodingHistoryMessage
{
    /// <summary>The message role (e.g. <c>user</c>, <c>assistant</c>).</summary>
    public required string Role { get; init; }

    /// <summary>The message content text.</summary>
    public required string Content { get; init; }
}
