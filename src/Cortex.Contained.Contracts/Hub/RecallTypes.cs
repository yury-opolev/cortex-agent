namespace Cortex.Contained.Contracts.Hub;

/// <summary>String values of <see cref="HistoryPurgeRequestDto.DependentMemories"/>.</summary>
public static class DependentMemoryActions
{
    /// <summary>Quarantine dependent memories for the owner's review (D10, the default).</summary>
    public const string Review = "review";

    /// <summary>Erase dependent memories physically as part of the purge (owner opt-in per purge).</summary>
    public const string Delete = "delete";

    /// <summary>True for <see cref="Review"/> or <see cref="Delete"/> (case-insensitive), or null/empty (review).</summary>
    public static bool IsValid(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value, Review, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="value"/> asks for deletion.</summary>
    public static bool IsDelete(string? value) => string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The effective recall settings of one agent (per tenant): configured defaults overlaid with the
/// owner's runtime choices persisted in the agent's state volume.
/// </summary>
public sealed record RecallSettingsDto
{
    /// <summary><c>History:Enabled</c>: projector running and recall tools exposed.</summary>
    public bool HistoryEnabled { get; init; } = true;

    /// <summary>What <c>history_search mode=auto</c> means: <c>hybrid</c> / <c>auto</c> (hybrid when embeddings are available, else keyword) or <c>keyword</c>.</summary>
    public string HistorySearchMode { get; init; } = "hybrid";

    /// <summary><c>History:Dense:Enabled</c>: chunks are embedded and hybrid search is possible.</summary>
    public bool HistoryDenseEnabled { get; init; } = true;

    /// <summary><c>MemoryMcp:Search:DefaultMode</c>: <c>dense</c>, <c>hybrid</c> or <c>keyword</c>.</summary>
    public string MemorySearchMode { get; init; } = "hybrid";

    /// <summary><c>Reranker:History:TopN</c>.</summary>
    public int RerankHistoryTopN { get; init; }

    /// <summary><c>Reranker:History:MaxDocChars</c>.</summary>
    public int RerankHistoryMaxDocChars { get; init; }

    /// <summary><c>Reranker:Memory:TopN</c>.</summary>
    public int RerankMemoryTopN { get; init; }

    /// <summary><c>Reranker:Memory:MaxDocChars</c>.</summary>
    public int RerankMemoryMaxDocChars { get; init; }

    /// <summary><c>Reranker:TimeoutMs</c>.</summary>
    public int RerankTimeoutMs { get; init; }

    /// <summary>Names of the settings that carry a persisted runtime override (the rest are configured defaults).</summary>
    public IReadOnlyList<string> Overridden { get; init; } = [];
}

/// <summary>
/// A change to the recall settings. Null members are left unchanged; <see cref="ResetToDefaults"/>
/// first drops every persisted override (the configured defaults apply again).
/// </summary>
public sealed record RecallSettingsPatchDto
{
    /// <summary>Drop every persisted override before applying the other members.</summary>
    public bool ResetToDefaults { get; init; }

    public bool? HistoryEnabled { get; init; }

    public string? HistorySearchMode { get; init; }

    public bool? HistoryDenseEnabled { get; init; }

    public string? MemorySearchMode { get; init; }

    public int? RerankHistoryTopN { get; init; }

    public int? RerankHistoryMaxDocChars { get; init; }

    public int? RerankMemoryTopN { get; init; }

    public int? RerankMemoryMaxDocChars { get; init; }

    public int? RerankTimeoutMs { get; init; }
}

/// <summary>Outcome of <c>UpdateRecallSettings</c>.</summary>
public sealed record RecallSettingsUpdateResultDto
{
    /// <summary>False when validation failed; nothing was persisted.</summary>
    public bool Applied { get; init; }

    /// <summary>Validation errors (empty when applied).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Consequences the owner should know about (for example an index rebuild).</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>The effective settings after the update.</summary>
    public required RecallSettingsDto Settings { get; init; }
}

/// <summary>Live state of the recall stack of one agent.</summary>
public sealed record RecallStatusDto
{
    public required RecallSettingsDto Settings { get; init; }

    public required HistoryIndexStatusDto History { get; init; }

    public required DenseEmbeddingStatusDto Dense { get; init; }

    public required RerankerStatusDto Reranker { get; init; }

    public required EventBackfillStatusDto EventBackfill { get; init; }

    /// <summary>Curated memories currently hidden for review (D10).</summary>
    public int QuarantinedMemories { get; init; }
}

/// <summary>History index build progress (the <c>/health</c> history block).</summary>
public sealed record HistoryIndexStatusDto
{
    public bool Enabled { get; init; }

    public bool ToolsEnabled { get; init; }

    /// <summary><c>open</c>, <c>awaitingKey</c> or <c>failed</c>.</summary>
    public string Storage { get; init; } = "open";

    public long? Generation { get; init; }

    public bool BuildComplete { get; init; }

    public bool BuildInProgress { get; init; }

    public long CheckpointLag { get; init; }

    public long JournalBacklog { get; init; }

    public bool Halted { get; init; }

    public string? LastError { get; init; }

    public int PendingErasureTasks { get; init; }

    public IReadOnlyList<string> PendingErasureSteps { get; init; } = [];

    public int MaintenancePendingTasks { get; init; }
}

/// <summary>Dense embedding progress of the served history generation.</summary>
public sealed record DenseEmbeddingStatusDto
{
    /// <summary><c>off</c>, <c>on</c> or <c>unavailable: reason</c>.</summary>
    public string State { get; init; } = "off";

    public long Embedded { get; init; }

    public long Total { get; init; }

    public long Pending { get; init; }

    public long Failed { get; init; }

    public DateTimeOffset? OldestPendingQueuedAt { get; init; }

    public string? LastError { get; init; }
}

/// <summary>Agent-side reranker health (the Bridge adds the sidecar state).</summary>
public sealed record RerankerStatusDto
{
    /// <summary>Whether the Bridge granted the reranker.</summary>
    public bool Enabled { get; init; }

    /// <summary><c>off</c>, <c>ok</c>, <c>circuit-open</c>, <c>half-open</c> or <c>unavailable: reason</c>.</summary>
    public string State { get; init; } = "off";

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? CircuitOpenUntil { get; init; }

    public string? LastError { get; init; }

    public double? LastLatencyMs { get; init; }

    public DateTimeOffset? LastCallAt { get; init; }
}

/// <summary>Event backfill state.</summary>
public sealed record EventBackfillStatusDto
{
    public bool Running { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public int? LastScanned { get; init; }

    public int? LastInserted { get; init; }

    public long? LastElapsedMs { get; init; }
}

/// <summary>What a history deletion would remove (shown before the owner confirms).</summary>
public sealed record HistoryPurgePreviewDto
{
    /// <summary>Messages the request would delete.</summary>
    public int Messages { get; init; }

    /// <summary>Curated memories created in the affected period (not already kept) — quarantined or deleted per the owner's choice.</summary>
    public int DependentMemories { get; init; }

    /// <summary>Oldest affected message, or null.</summary>
    public DateTimeOffset? RangeFrom { get; init; }

    /// <summary>Newest affected message, or null.</summary>
    public DateTimeOffset? RangeTo { get; init; }
}

/// <summary>One quarantined memory with the purge that quarantined it.</summary>
public sealed record QuarantinedMemoryDto
{
    public required MemoryItem Memory { get; init; }

    /// <summary>Erasure workflow id of the (latest) purge whose window contains the memory.</summary>
    public required string PurgeId { get; init; }

    /// <summary>When that purge ran.</summary>
    public DateTimeOffset PurgedAt { get; init; }

    /// <summary>The purged channel, or null for an all-channel purge.</summary>
    public string? PurgeChannelId { get; init; }

    /// <summary>Start of the quarantine window.</summary>
    public DateTimeOffset WindowFrom { get; init; }

    /// <summary>End of the quarantine window.</summary>
    public DateTimeOffset WindowTo { get; init; }
}

/// <summary>One page of the quarantined memories awaiting review.</summary>
public sealed record QuarantineReviewListDto
{
    public required IReadOnlyList<QuarantinedMemoryDto> Items { get; init; }

    /// <summary>Quarantined memories in total (the page holds at most <see cref="Limit"/> of them).</summary>
    public int Total { get; init; }

    /// <summary>Entries skipped before this page.</summary>
    public int Offset { get; init; }

    /// <summary>The page size that was applied.</summary>
    public int Limit { get; init; }
}

/// <summary>Outcome of a bulk keep or delete of quarantined memories.</summary>
public sealed record QuarantineDecisionResultDto
{
    /// <summary>Memories kept or deleted.</summary>
    public int Affected { get; init; }

    /// <summary>Ids ignored because they were not quarantined (or no longer exist).</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}
