namespace Cortex.Contained.Agent.Host.History;

/// <summary>Kind of change recorded in <c>HistoryJournal</c>.</summary>
public enum HistoryJournalOp
{
    /// <summary>Rows were inserted or changed; <see cref="HistoryJournalEntry.MessageIds"/> lists them.</summary>
    Upsert,

    /// <summary>Rows were deleted for a channel and/or a time bound, or all rows.</summary>
    Purge,
}

/// <summary>One content-free change-journal row (§8).</summary>
/// <param name="Seq">Monotonic sequence number.</param>
/// <param name="Op">The kind of change.</param>
/// <param name="ChannelId">Affected channel; null for an all-channel purge.</param>
/// <param name="MessageIds">Upserted row ids; empty for purges.</param>
/// <param name="FromTs">Inclusive lower time bound of a purge, or null.</param>
/// <param name="ToTs">Exclusive upper time bound of a purge, or null.</param>
/// <param name="At">When the change was written.</param>
public sealed record HistoryJournalEntry(
    long Seq,
    HistoryJournalOp Op,
    string? ChannelId,
    IReadOnlyList<long> MessageIds,
    DateTimeOffset? FromTs,
    DateTimeOffset? ToTs,
    DateTimeOffset At)
{
    /// <summary>The <c>Meta.PurgeGeneration</c> a purge entry committed; null for upserts.</summary>
    public long? PurgeGeneration { get; init; }
}

/// <summary>Source identity and journal bounds stored in <c>messages.db</c> <c>Meta</c>.</summary>
public sealed record HistorySourceMeta(
    string DbInstanceId,
    long RestoreGeneration,
    long JournalHead,
    long JournalPrunedThrough)
{
    /// <summary>Durable purge counter, incremented inside every purge transaction.</summary>
    public long PurgeGeneration { get; init; }
}
