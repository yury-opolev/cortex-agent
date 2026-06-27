namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Request to fetch a coding session's transcript. When <see cref="SinceIndex"/> is null the
/// full history is returned; otherwise only the messages after that cursor (plus a
/// <c>nextIndex</c>) are returned.
/// </summary>
public sealed record CodingHistoryRequest
{
    public required string SessionId { get; init; }

    /// <summary>
    /// Cursor for an incremental fetch: return only messages after this index. Null requests
    /// the full transcript.
    /// </summary>
    public int? SinceIndex { get; init; }
}
