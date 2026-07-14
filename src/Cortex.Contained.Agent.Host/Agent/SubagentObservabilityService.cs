using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Projects <see cref="SubagentSessionStore"/> + <see cref="SubagentRunnerRegistry"/> state into
/// content-free <see cref="SubagentWorkerSnapshot"/>/<see cref="SubagentAggregateSnapshot"/> DTOs
/// for generic operational observability (a future IcM dashboard, the agent's own workspace
/// files). NEVER surfaces prompt, message history, result, or eval text — only identifiers,
/// lifecycle state, and timing/counters computed from <see cref="TimeProvider"/>.
/// <para>
/// Self-wires an aggregate provider onto the injected <see cref="AgentMetrics"/> at construction
/// time so <c>/health</c> picks up live subagent-pool counts (using
/// <see cref="DefaultStaleAfterSeconds"/>) without any other subsystem needing to know this
/// service exists — mirrors how <c>AgentRuntime</c> self-registers the active-conversations
/// provider.
/// </para>
/// </summary>
public sealed class SubagentObservabilityService
{
    /// <summary>Staleness threshold (seconds) used for the aggregate embedded in <c>/health</c>.</summary>
    public const int DefaultStaleAfterSeconds = 600;

    /// <summary>Lower bound accepted for a caller-supplied paging limit.</summary>
    private const int MinLimit = 1;

    /// <summary>Upper bound accepted for a caller-supplied paging limit.</summary>
    private const int MaxLimit = 1000;

    /// <summary>
    /// SECURITY: <see cref="SubagentTask.Description"/> is populated verbatim from an LLM-supplied
    /// subagent task description with no length cap upstream — only a prompt convention asks for
    /// "3-5 words". Cap it here, at the observability MAPPER boundary, so a misbehaving or
    /// prompt-injected task cannot surface an oversized or embedded-content label through the
    /// generic operations endpoint.
    /// </summary>
    private const int MaxDescriptionLength = 200;

    private const string TruncationSuffix = "…";

    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly TimeProvider timeProvider;

    public SubagentObservabilityService(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        AgentMetrics metrics,
        TimeProvider timeProvider)
    {
        this.store = store;
        this.registry = registry;
        this.timeProvider = timeProvider;

        metrics.SetSubagentAggregateProvider(() => this.GetAggregate(DefaultStaleAfterSeconds));
    }

    /// <summary>
    /// Builds the full observability response for <paramref name="query"/>: a paged worker
    /// list (clamped limit, optionally terminal-inclusive) plus the pool-wide aggregate
    /// (always computed over every currently-active task, independent of the page).
    /// </summary>
    public SubagentObservabilitySnapshot GetSnapshot(SubagentSnapshotQuery query)
    {
        var limit = Math.Clamp(query.Limit, MinLimit, MaxLimit);
        var staleAfterSeconds = Math.Max(query.StaleAfterSeconds, MinLimit);
        var now = this.timeProvider.GetUtcNow();

        var recent = this.store.GetRecent(limit, query.IncludeTerminal);
        var workers = recent.Select(task => Project(task, now, staleAfterSeconds)).ToList();

        return new SubagentObservabilitySnapshot
        {
            Workers = workers,
            Aggregate = this.BuildAggregate(now, staleAfterSeconds),
        };
    }

    /// <summary>Pool-wide aggregate only, using <paramref name="staleAfterSeconds"/> as the staleness threshold.</summary>
    public SubagentAggregateSnapshot GetAggregate(int staleAfterSeconds)
    {
        var now = this.timeProvider.GetUtcNow();
        return this.BuildAggregate(now, Math.Max(staleAfterSeconds, MinLimit));
    }

    private SubagentAggregateSnapshot BuildAggregate(DateTimeOffset now, int staleAfterSeconds)
    {
        var activeTasks = this.store.GetActive();

        var countsByState = new Dictionary<string, int>(StringComparer.Ordinal);
        var queueDepth = 0;
        var staleActiveCount = 0;
        var restartCount = 0;
        DateTimeOffset? oldestQueuedCreatedAt = null;
        long? longestActiveDurationMs = null;

        foreach (var task in activeTasks)
        {
            var stateKey = task.State.ToStorageValue();
            countsByState[stateKey] = countsByState.GetValueOrDefault(stateKey) + 1;
            restartCount += task.RestartCount;

            if ((now - task.LastProgressAt).TotalSeconds >= staleAfterSeconds)
            {
                staleActiveCount++;
            }

            if (task.State == SubagentTaskState.Queued)
            {
                queueDepth++;
                if (oldestQueuedCreatedAt is null || task.CreatedAt < oldestQueuedCreatedAt)
                {
                    oldestQueuedCreatedAt = task.CreatedAt;
                }
            }

            if (task.State is SubagentTaskState.Running or SubagentTaskState.Revising)
            {
                var duration = ElapsedMs(task.StartedAt ?? task.CreatedAt, now);
                if (longestActiveDurationMs is null || duration > longestActiveDurationMs)
                {
                    longestActiveDurationMs = duration;
                }
            }
        }

        return new SubagentAggregateSnapshot
        {
            CountsByState = countsByState,
            QueueDepth = queueDepth,
            ActiveCount = this.registry.ActiveCount,
            MaxConcurrency = this.registry.MaxConcurrent,
            StaleActiveCount = staleActiveCount,
            OldestQueuedAgeMs = oldestQueuedCreatedAt is null ? null : ElapsedMs(oldestQueuedCreatedAt.Value, now),
            LongestActiveDurationMs = longestActiveDurationMs,
            RestartCount = restartCount,
        };
    }

    private static SubagentWorkerSnapshot Project(SubagentTask task, DateTimeOffset now, int staleAfterSeconds)
    {
        var isActive = task.State is SubagentTaskState.Queued or SubagentTaskState.Running or SubagentTaskState.Revising;
        var endTime = task.CompletedAt ?? now;
        var stalenessMs = ElapsedMs(task.LastProgressAt, now);
        var isStale = isActive && stalenessMs >= staleAfterSeconds * 1000L;

        return new SubagentWorkerSnapshot
        {
            TaskId = task.TaskId,
            ParentConversationId = task.ParentConversation,
            ParentChannelId = task.ParentChannel,
            Description = TruncateDescription(task.Description),
            State = task.State.ToStorageValue(),
            CreatedAt = task.CreatedAt,
            StartedAt = task.StartedAt,
            LastProgressAt = task.LastProgressAt,
            CompletedAt = task.CompletedAt,
            DurationMs = ElapsedMs(task.CreatedAt, endTime),
            StalenessMs = stalenessMs,
            RestartCount = task.RestartCount,
            Rounds = task.Rounds,
            IsStale = isStale,
        };
    }

    /// <summary>Milliseconds between <paramref name="from"/> and <paramref name="to"/>, floored at zero.</summary>
    private static long ElapsedMs(DateTimeOffset from, DateTimeOffset to)
        => Math.Max(0, (long)(to - from).TotalMilliseconds);

    /// <summary>Caps a task description to <see cref="MaxDescriptionLength"/> characters, appending an ellipsis when truncated.</summary>
    private static string TruncateDescription(string description)
        => description.Length <= MaxDescriptionLength
            ? description
            : string.Concat(description.AsSpan(0, MaxDescriptionLength), TruncationSuffix);
}
