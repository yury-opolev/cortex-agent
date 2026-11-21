namespace Cortex.Contained.Contracts.Hub;

/// <summary>A history deletion request: one channel and/or everything older than a cutoff, or everything.</summary>
public sealed record HistoryPurgeRequestDto
{
    /// <summary>Only this channel; null for every channel.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Only messages older than this instant; null for all times.</summary>
    public DateTimeOffset? OlderThan { get; init; }
}

/// <summary>Outcome of a history deletion, including the erasure status and D10 quarantine count.</summary>
public sealed record HistoryPurgeResultDto
{
    /// <summary>Messages deleted.</summary>
    public int Deleted { get; init; }

    /// <summary>True when every cleanup step (index, WAL, spill files, subagent records, evaluation artifacts) finished.</summary>
    public bool ErasureComplete { get; init; }

    /// <summary>Cleanup steps that will resume automatically.</summary>
    public IReadOnlyList<string> CleanupPending { get; init; } = [];

    /// <summary>Curated memories created in the deleted period that are now hidden until reviewed.</summary>
    public int QuarantinedMemories { get; init; }
}

/// <summary>Outcome of the explicit event backfill (legacy_import events for rows stored before the event log).</summary>
public sealed record EventBackfillResultDto
{
    /// <summary>Messages scanned.</summary>
    public int Scanned { get; init; }

    /// <summary>Messages that received a legacy_import event (0 on a repeated run).</summary>
    public int Inserted { get; init; }

    /// <summary>Duration of the maintenance window, in milliseconds.</summary>
    public long ElapsedMs { get; init; }
}