namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// A coding session's transcript, returned by the <c>coding_session_history</c> tool.
/// For a full-history request <see cref="NextIndex"/> is <c>null</c>; for an incremental
/// (since-cursor) request it carries the cursor the caller passes next.
/// </summary>
public sealed record CodingHistory
{
    /// <summary>The transcript messages (full, or the slice since the requested cursor).</summary>
    public IReadOnlyList<CodingHistoryMessage> Messages { get; init; } = [];

    /// <summary>
    /// The cursor to pass as <c>sinceIndex</c> on the next incremental fetch. Only set for an
    /// incremental request (when <c>sinceIndex</c> was supplied); <c>null</c> for full history.
    /// </summary>
    public int? NextIndex { get; init; }
}
