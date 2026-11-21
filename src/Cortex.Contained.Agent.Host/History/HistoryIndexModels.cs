namespace Cortex.Contained.Agent.Host.History;

/// <summary>The single <c>projector_state</c> row of a generation file (§9.1).</summary>
public sealed record ProjectorState(
    long Generation,
    long CheckpointJournalSeq,
    string SourceInstanceId,
    long SourceRestoreGeneration,
    string ConfigFingerprint,
    bool BuildComplete,
    string CoverageJson)
{
    /// <summary>Highest source <c>PurgeGeneration</c> this generation has applied (§4.1 fencing).</summary>
    public long AppliedPurgeGeneration { get; init; }
}

/// <summary>Indexed time range of one channel (canonical UTC timestamps).</summary>
public sealed record ChannelCoverage(string ChannelId, string From, string To);

/// <summary>One index transaction: whole-channel replacements plus checkpoint/build bookkeeping.</summary>
/// <param name="Channels">Channel → its complete new segment list (empty list removes the channel).</param>
public sealed record IndexBatch(IReadOnlyDictionary<string, IReadOnlyList<HistorySegment>> Channels)
{
    /// <summary>New <c>CheckpointJournalSeq</c>, or null to keep it.</summary>
    public long? CheckpointSeq { get; init; }

    /// <summary>Channels to record as done in an in-progress build.</summary>
    public IReadOnlyCollection<string> BuildProgress { get; init; } = [];

    /// <summary>Sets <c>BuildComplete=1</c> and clears build progress.</summary>
    public bool MarkBuildComplete { get; init; }

    /// <summary>Purge generation applied by this batch (never lowers the stored value), or null.</summary>
    public long? AppliedPurgeGeneration { get; init; }
}

/// <summary>A segment row returned by an index query.</summary>
public sealed record IndexedSegment(
    long SegId,
    string ChannelId,
    string StartTs,
    string EndTs,
    string LocalDate,
    long FirstMsgId,
    long LastMsgId,
    IReadOnlyList<long> MsgIds,
    bool HasInternal,
    bool HasRecallEcho,
    string Fingerprint,
    double Score);

/// <summary>Segment-level filters; timestamps are canonical UTC strings.</summary>
/// <param name="ChannelId">Only this channel, or null.</param>
/// <param name="Since">Segments ending at or after this instant, or null.</param>
/// <param name="Until">Segments starting before this instant, or null.</param>
public sealed record SegmentFilter(string? ChannelId, string? Since, string? Until);
