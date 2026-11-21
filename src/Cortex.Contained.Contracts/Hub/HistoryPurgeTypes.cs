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
