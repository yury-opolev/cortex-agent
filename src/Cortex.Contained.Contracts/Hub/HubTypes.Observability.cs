namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// Content-free operational snapshot of ONE subagent worker task, exposed to generic
/// observability surfaces (a future operations dashboard, the agent's own workspace files).
/// Deliberately carries NO prompt, message history, result, or eval text — only
/// identifiers, lifecycle state, and timing/counters. See
/// <see cref="ISubagentHub.GetSubagentSnapshots"/> for how this is produced.
/// </summary>
public sealed record SubagentWorkerSnapshot
{
    /// <summary>Unique task identifier (e.g. "sa-7f3a...").</summary>
    public required string TaskId { get; init; }

    /// <summary>Conversation ID of the parent that spawned this task.</summary>
    public required string ParentConversationId { get; init; }

    /// <summary>Channel ID the terminal result is delivered to (e.g. "webchat-default").</summary>
    public required string ParentChannelId { get; init; }

    /// <summary>Short human-readable description (3-5 words). Never the full prompt.</summary>
    public required string Description { get; init; }

    /// <summary>Lifecycle state, lower-case wire value (e.g. "queued", "running", "completed").</summary>
    public required string State { get; init; }

    /// <summary>When the task was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When execution first started (survives restarts). Null if never started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Last time the task made observable progress (stall-detection input).</summary>
    public required DateTimeOffset LastProgressAt { get; init; }

    /// <summary>When the task reached a terminal state (completed/failed/cancelled). Null while active.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Elapsed milliseconds from creation to completion (or now, if still active).</summary>
    public long DurationMs { get; init; }

    /// <summary>Milliseconds since the task last made observable progress.</summary>
    public long StalenessMs { get; init; }

    /// <summary>Number of times execution was restarted (crash recovery or requeue).</summary>
    public int RestartCount { get; init; }

    /// <summary>Number of LLM rounds executed by the subagent runner.</summary>
    public int Rounds { get; init; }

    /// <summary>True when the task is active (queued/running/revising) and has exceeded the staleness threshold.</summary>
    public bool IsStale { get; init; }
}

/// <summary>
/// Aggregate, content-free counts over the subagent worker pool — computed from every
/// currently-active (queued/running/revising) task, independent of any per-request paging
/// applied to the accompanying worker list. Surfaced both by the live
/// <c>/operations/subagents</c> endpoint (via <see cref="SubagentObservabilitySnapshot"/>) and
/// by <c>/health</c> (via <see cref="AgentMetricsSnapshot.Subagents"/>, a point-in-time gauge
/// using the service's default staleness threshold).
/// </summary>
public sealed record SubagentAggregateSnapshot
{
    /// <summary>Count of active tasks keyed by lower-case state (e.g. "queued", "running", "revising").</summary>
    public required IReadOnlyDictionary<string, int> CountsByState { get; init; }

    /// <summary>Number of tasks currently waiting for a concurrency slot.</summary>
    public required int QueueDepth { get; init; }

    /// <summary>Number of tasks currently occupying a concurrency slot (executing right now).</summary>
    public required int ActiveCount { get; init; }

    /// <summary>The current live concurrency cap.</summary>
    public required int MaxConcurrency { get; init; }

    /// <summary>Number of active tasks whose staleness has exceeded the configured threshold.</summary>
    public required int StaleActiveCount { get; init; }

    /// <summary>Age of the oldest queued task in milliseconds, or null when nothing is queued.</summary>
    public long? OldestQueuedAgeMs { get; init; }

    /// <summary>Longest running duration among currently-executing tasks in milliseconds, or null when none are executing.</summary>
    public long? LongestActiveDurationMs { get; init; }

    /// <summary>Sum of restart counts across all currently-active tasks.</summary>
    public required int RestartCount { get; init; }
}

/// <summary>
/// Full response to a live subagent-observability request: the (possibly filtered/limited)
/// worker list plus the pool-wide aggregate. See <see cref="ISubagentHub.GetSubagentSnapshots"/>.
/// </summary>
public sealed record SubagentObservabilitySnapshot
{
    /// <summary>The requested page of workers, newest first.</summary>
    public required IReadOnlyList<SubagentWorkerSnapshot> Workers { get; init; }

    /// <summary>Pool-wide aggregate counts, unaffected by the worker list's paging.</summary>
    public required SubagentAggregateSnapshot Aggregate { get; init; }
}

/// <summary>Bridge → Agent query parameters for <see cref="ISubagentHub.GetSubagentSnapshots"/>.</summary>
public sealed record SubagentSnapshotQuery
{
    /// <summary>Maximum number of workers to return. Clamped server-side to [1, 1000].</summary>
    public int Limit { get; init; } = 100;

    /// <summary>When false, only active (queued/running/revising) workers are returned.</summary>
    public bool IncludeTerminal { get; init; } = true;

    /// <summary>Seconds of no progress before an active worker is flagged stale. Clamped server-side to a minimum of 1.</summary>
    public int StaleAfterSeconds { get; init; } = 600;
}

/// <summary>
/// Aggregate, content-free counts of approval-gated MCP actions, keyed by wire status (e.g.
/// "proposed", "outcome_unknown"). Surfaced by <c>/health</c> via <see cref="HealthInfo.McpActions"/>.
/// Never includes canonical arguments, result content, or error text.
/// </summary>
public sealed record McpActionAggregateSnapshot
{
    /// <summary>Count of actions keyed by wire status (see McpActionWireStatus on the Bridge).</summary>
    public required IReadOnlyDictionary<string, int> CountsByState { get; init; }

    /// <summary>Total number of actions the aggregate was computed over.</summary>
    public required int TotalCount { get; init; }
}
