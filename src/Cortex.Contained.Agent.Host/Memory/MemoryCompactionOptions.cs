namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Configuration options for the <see cref="MemoryCompactionService"/>.
/// Bound from the "MemoryCompaction" section of appsettings.json.
/// </summary>
public sealed class MemoryCompactionOptions
{
    public const string SectionName = "MemoryCompaction";

    /// <summary>
    /// Whether the compaction service is enabled. Default: true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Preferred time of day (local time) to run compaction, in "HH:mm" format.
    /// If not set, compaction runs 5 minutes after startup and every
    /// <see cref="IntervalHours"/> hours thereafter.
    /// When set, the first run is scheduled at the next occurrence of this time,
    /// and subsequent runs happen every <see cref="IntervalHours"/> hours.
    /// Example: "03:00" for 3 AM.
    /// </summary>
    public string? PreferredTimeOfDay { get; set; }

    /// <summary>
    /// How often the compaction sweep runs, in hours. Default: 24.
    /// </summary>
    public int IntervalHours { get; set; } = 24;

    /// <summary>
    /// Similarity threshold for compaction (0.0–1.0). Memories with similarity
    /// above this threshold are considered near-duplicates. Default: 0.7.
    /// Higher than the consolidation threshold (0.3) because compaction should
    /// only merge memories that are clearly about the same thing.
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.7f;

}
