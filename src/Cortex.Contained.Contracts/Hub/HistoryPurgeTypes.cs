namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// A history deletion request: one channel and/or a time range (<see cref="From"/> inclusive,
/// <see cref="OlderThan"/> exclusive), or everything.
/// </summary>
public sealed record HistoryPurgeRequestDto
{
    /// <summary>Only this channel; null for every channel.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Only messages older than this instant; null for no upper bound.</summary>
    public DateTimeOffset? OlderThan { get; init; }

    /// <summary>Only messages at or after this instant; null for no lower bound.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>
    /// What happens to curated memories created in the purged period:
    /// <see cref="DependentMemoryActions.Review"/> (default, quarantine) or <see cref="DependentMemoryActions.Delete"/>.
    /// </summary>
    public string DependentMemories { get; init; } = DependentMemoryActions.Review;

    /// <summary>
    /// The message count the preview reported for this scope. The agent refuses the deletion when
    /// markedly more messages are now in scope (the scope changed since the preview); null skips the check.
    /// </summary>
    public int? ExpectedMessages { get; init; }
}

/// <summary>Marker of the agent's "scope changed since the preview" refusal, which the Bridge reports as 409.</summary>
public static class HistoryPurgeErrors
{
    /// <summary>Prefix of the error message.</summary>
    public const string ScopeChangedPrefix = "scope-changed:";
}

/// <summary>Outcome of a history deletion, including the erasure status and the dependent-memory outcome.</summary>
public sealed record HistoryPurgeResultDto
{
    /// <summary>Messages deleted.</summary>
    public int Deleted { get; init; }

    /// <summary>True when every cleanup step (index, WAL, spill files, subagent records, evaluation artifacts, dependent memories) finished.</summary>
    public bool ErasureComplete { get; init; }

    /// <summary>Cleanup steps that will resume automatically.</summary>
    public IReadOnlyList<string> CleanupPending { get; init; } = [];

    /// <summary>Curated memories created in the deleted period that are now hidden until reviewed.</summary>
    public int QuarantinedMemories { get; init; }

    /// <summary>Curated memories created in the deleted period that were erased with the history (owner opt-in).</summary>
    public int DeletedMemories { get; init; }

    /// <summary>The dependent-memory action that was applied (<c>review</c> or <c>delete</c>).</summary>
    public string DependentMemories { get; init; } = DependentMemoryActions.Review;
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