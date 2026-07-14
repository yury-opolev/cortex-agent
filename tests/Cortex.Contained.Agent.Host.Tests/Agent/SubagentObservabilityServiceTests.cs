using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

/// <summary>
/// Proves the content-free projection contract of <see cref="SubagentObservabilityService"/>:
/// deterministic duration/staleness math via <see cref="FakeTimeProvider"/>, correct paging vs.
/// pool-wide aggregation, and that the aggregate self-registers on <see cref="AgentMetrics"/>.
/// </summary>
public sealed class SubagentObservabilityServiceTests : IDisposable
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "subagent-observability-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly AgentMetrics _metrics = new();
    private readonly FakeTimeProvider _timeProvider = new(StartTime);
    private readonly SubagentObservabilityService _service;

    public SubagentObservabilityServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance);
        _service = new SubagentObservabilityService(_store, _registry, _metrics, _timeProvider);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static SubagentTask CreateTask(
        string taskId,
        SubagentTaskState state,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? lastProgressAt = null,
        DateTimeOffset? completedAt = null,
        int restartCount = 0,
        int rounds = 0)
    {
        return new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "p",
            State = state,
            CreatedAt = createdAt,
            StartedAt = startedAt,
            LastProgressAt = lastProgressAt ?? createdAt,
            CompletedAt = completedAt,
            RestartCount = restartCount,
            Rounds = rounds,
        };
    }

    // ── Worker projection: no sensitive content, deterministic duration/staleness ──

    [Fact]
    public void GetSnapshot_ProjectsWorker_WithNoPromptOrMessageFields()
    {
        // The projection reads only from SubagentTask fields that exist on
        // SubagentWorkerSnapshot — there is no way for Prompt/Messages/Result/EvalResponse
        // to leak through, since the DTO simply has no such members (proven at the type level
        // in ObservabilityDtoTests). This test proves the mapper populates every non-sensitive
        // field faithfully.
        _store.Create(CreateTask("sa-1", SubagentTaskState.Running, StartTime, startedAt: StartTime));

        var snapshot = _service.GetSnapshot(new() { IncludeTerminal = true });

        var worker = Assert.Single(snapshot.Workers);
        Assert.Equal("sa-1", worker.TaskId);
        Assert.Equal("conv-1", worker.ParentConversationId);
        Assert.Equal("webchat-default", worker.ParentChannelId);
        Assert.Equal("d", worker.Description);
        Assert.Equal("running", worker.State);
    }

    [Fact]
    public void GetSnapshot_ActiveTask_DurationMs_IsElapsedSinceCreation()
    {
        _store.Create(CreateTask("sa-1", SubagentTaskState.Running, StartTime));
        _timeProvider.Advance(TimeSpan.FromSeconds(30));

        var snapshot = _service.GetSnapshot(new());

        var worker = Assert.Single(snapshot.Workers);
        Assert.Equal(30_000, worker.DurationMs);
    }

    [Fact]
    public void GetSnapshot_CompletedTask_DurationMs_IsFrozenAtCompletion()
    {
        var completedAt = StartTime.AddSeconds(10);
        _store.Create(CreateTask("sa-1", SubagentTaskState.Completed, StartTime, completedAt: completedAt));
        _timeProvider.Advance(TimeSpan.FromMinutes(5)); // time keeps moving after completion

        var snapshot = _service.GetSnapshot(new());

        var worker = Assert.Single(snapshot.Workers);
        Assert.Equal(10_000, worker.DurationMs); // NOT affected by time elapsed after completion
    }

    [Fact]
    public void GetSnapshot_StalenessMs_IsElapsedSinceLastProgress()
    {
        _store.Create(CreateTask("sa-1", SubagentTaskState.Running, StartTime, lastProgressAt: StartTime));
        _timeProvider.Advance(TimeSpan.FromSeconds(45));

        var snapshot = _service.GetSnapshot(new());

        var worker = Assert.Single(snapshot.Workers);
        Assert.Equal(45_000, worker.StalenessMs);
    }

    [Fact]
    public void GetSnapshot_ActiveTaskPastStaleThreshold_IsStaleTrue()
    {
        _store.Create(CreateTask("sa-1", SubagentTaskState.Running, StartTime, lastProgressAt: StartTime));
        _timeProvider.Advance(TimeSpan.FromSeconds(700));

        var snapshot = _service.GetSnapshot(new() { StaleAfterSeconds = 600 });

        Assert.True(Assert.Single(snapshot.Workers).IsStale);
    }

    [Fact]
    public void GetSnapshot_ActiveTaskWithinStaleThreshold_IsStaleFalse()
    {
        _store.Create(CreateTask("sa-1", SubagentTaskState.Running, StartTime, lastProgressAt: StartTime));
        _timeProvider.Advance(TimeSpan.FromSeconds(100));

        var snapshot = _service.GetSnapshot(new() { StaleAfterSeconds = 600 });

        Assert.False(Assert.Single(snapshot.Workers).IsStale);
    }

    [Fact]
    public void GetSnapshot_TerminalTaskPastStaleThreshold_IsStaleFalse()
    {
        // Staleness only ever applies to ACTIVE work — a completed task sitting in history
        // must never be flagged stale no matter how long ago it finished.
        _store.Create(CreateTask(
            "sa-1", SubagentTaskState.Completed, StartTime,
            lastProgressAt: StartTime, completedAt: StartTime.AddSeconds(1)));
        _timeProvider.Advance(TimeSpan.FromDays(30));

        var snapshot = _service.GetSnapshot(new() { StaleAfterSeconds = 600 });

        Assert.False(Assert.Single(snapshot.Workers).IsStale);
    }

    // ── Paging vs. IncludeTerminal ───────────────────────────────────────

    [Fact]
    public void GetSnapshot_IncludeTerminalFalse_OmitsCompletedWorkers()
    {
        _store.Create(CreateTask("sa-active", SubagentTaskState.Running, StartTime));
        _store.Create(CreateTask("sa-done", SubagentTaskState.Completed, StartTime, completedAt: StartTime));

        var snapshot = _service.GetSnapshot(new() { IncludeTerminal = false });

        Assert.Single(snapshot.Workers);
        Assert.Equal("sa-active", snapshot.Workers[0].TaskId);
    }

    [Fact]
    public void GetSnapshot_ClampsLimitToConfiguredBounds()
    {
        for (var i = 0; i < 3; i++)
        {
            _store.Create(CreateTask($"sa-{i}", SubagentTaskState.Queued, StartTime.AddSeconds(i)));
        }

        var snapshot = _service.GetSnapshot(new() { Limit = 1 });

        Assert.Single(snapshot.Workers);
    }

    // ── Aggregate: independent of paging ─────────────────────────────────

    [Fact]
    public void GetSnapshot_Aggregate_CountsAllActiveTasks_RegardlessOfPageLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Create(CreateTask($"sa-{i}", SubagentTaskState.Queued, StartTime.AddSeconds(i)));
        }

        var snapshot = _service.GetSnapshot(new() { Limit = 1 });

        Assert.Single(snapshot.Workers); // page is limited
        Assert.Equal(5, snapshot.Aggregate.QueueDepth); // aggregate is not
    }

    [Fact]
    public void GetSnapshot_Aggregate_CountsByState_GroupsActiveTasks()
    {
        _store.Create(CreateTask("sa-q1", SubagentTaskState.Queued, StartTime));
        _store.Create(CreateTask("sa-q2", SubagentTaskState.Queued, StartTime));
        _store.Create(CreateTask("sa-r1", SubagentTaskState.Running, StartTime));

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.Equal(2, aggregate.CountsByState["queued"]);
        Assert.Equal(1, aggregate.CountsByState["running"]);
    }

    [Fact]
    public void GetSnapshot_Aggregate_ExcludesTerminalTasksFromCounts()
    {
        _store.Create(CreateTask("sa-active", SubagentTaskState.Running, StartTime));
        _store.Create(CreateTask("sa-done", SubagentTaskState.Completed, StartTime, completedAt: StartTime));
        _store.Create(CreateTask("sa-failed", SubagentTaskState.Failed, StartTime, completedAt: StartTime));

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.False(aggregate.CountsByState.ContainsKey("completed"));
        Assert.False(aggregate.CountsByState.ContainsKey("failed"));
        Assert.Equal(1, aggregate.CountsByState["running"]);
    }

    [Fact]
    public void GetSnapshot_Aggregate_OldestQueuedAgeMs_UsesEarliestQueuedTask()
    {
        _store.Create(CreateTask("sa-newer", SubagentTaskState.Queued, StartTime.AddSeconds(10)));
        _store.Create(CreateTask("sa-older", SubagentTaskState.Queued, StartTime));
        _timeProvider.Advance(TimeSpan.FromSeconds(60));

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.Equal(60_000, aggregate.OldestQueuedAgeMs); // relative to sa-older's CreatedAt
    }

    [Fact]
    public void GetSnapshot_Aggregate_OldestQueuedAgeMs_NullWhenNothingQueued()
    {
        _store.Create(CreateTask("sa-running", SubagentTaskState.Running, StartTime));

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.Null(aggregate.OldestQueuedAgeMs);
    }

    [Fact]
    public void GetSnapshot_Aggregate_LongestActiveDurationMs_MaxOverRunningAndRevising()
    {
        _store.Create(CreateTask("sa-short", SubagentTaskState.Running, StartTime, startedAt: StartTime.AddSeconds(50)));
        _store.Create(CreateTask("sa-long", SubagentTaskState.Revising, StartTime, startedAt: StartTime));
        _store.Create(CreateTask("sa-queued", SubagentTaskState.Queued, StartTime)); // never counted
        _timeProvider.Advance(TimeSpan.FromSeconds(60));

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.Equal(60_000, aggregate.LongestActiveDurationMs); // sa-long: 60s since StartTime
    }

    [Fact]
    public void GetSnapshot_Aggregate_RestartCount_SumsAcrossActiveTasks()
    {
        _store.Create(CreateTask("sa-1", SubagentTaskState.Queued, StartTime, restartCount: 2));
        _store.Create(CreateTask("sa-2", SubagentTaskState.Running, StartTime, restartCount: 3));

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.Equal(5, aggregate.RestartCount);
    }

    [Fact]
    public void GetSnapshot_Aggregate_ActiveCountAndMaxConcurrency_ComeFromRegistry()
    {
        _registry.TryRegister("sa-running-1", CreateDummyRunner(), out _);

        var aggregate = _service.GetSnapshot(new()).Aggregate;

        Assert.Equal(1, aggregate.ActiveCount);
        Assert.Equal(5, aggregate.MaxConcurrency);
    }

    [Fact]
    public void GetSnapshot_Aggregate_StaleActiveCount_CountsOnlyStaleActiveTasks()
    {
        _store.Create(CreateTask("sa-stale", SubagentTaskState.Running, StartTime, lastProgressAt: StartTime));
        _store.Create(CreateTask("sa-fresh", SubagentTaskState.Running, StartTime, lastProgressAt: StartTime));
        _timeProvider.Advance(TimeSpan.FromSeconds(700));
        // Touch the "fresh" task's progress AFTER advancing, so only "sa-stale" is stale.
        _store.TouchProgress("sa-fresh");

        var aggregate = _service.GetSnapshot(new() { StaleAfterSeconds = 600 }).Aggregate;

        Assert.Equal(1, aggregate.StaleActiveCount);
    }

    // ── AgentMetrics self-registration ───────────────────────────────────

    [Fact]
    public void Constructor_SelfRegisters_AggregateProviderOnMetrics()
    {
        _store.Create(CreateTask("sa-1", SubagentTaskState.Queued, StartTime));

        var snapshot = _metrics.Snapshot();

        Assert.NotNull(snapshot.Subagents);
        Assert.Equal(1, snapshot.Subagents!.QueueDepth);
    }

    private static SubagentRunner CreateDummyRunner() => new(
        Substitute.For<ILlmClient>(),
        new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance),
        maxRounds: 10,
        logger: NullLogger<SubagentRunner>.Instance);
}
