using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class AgentMetricsTests
{
    // ── Defaults ──────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_FreshInstance_AllZero()
    {
        var metrics = new AgentMetrics();

        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.TotalMessagesProcessed);
        Assert.Equal(0, snapshot.ActiveConversations);
        Assert.Equal(0, snapshot.InboundQueueDepth);
        Assert.Equal(0, snapshot.InboundQueuePeak);
        Assert.Equal(0, snapshot.ExtractionQueueDepth);
        Assert.Equal(0, snapshot.ExtractionQueuePeak);
        Assert.Equal(0, snapshot.TokenRefreshSuccesses);
        Assert.Equal(0, snapshot.TokenRefreshFailures);
    }

    // ── Message counter ───────────────────────────────────────────────────

    [Fact]
    public void IncrementMessagesProcessed_IncrementsByOne()
    {
        var metrics = new AgentMetrics();

        metrics.IncrementMessagesProcessed();
        metrics.IncrementMessagesProcessed();
        metrics.IncrementMessagesProcessed();

        Assert.Equal(3, metrics.Snapshot().TotalMessagesProcessed);
    }

    [Fact]
    public async Task IncrementMessagesProcessed_IsAtomicUnderParallelLoad()
    {
        var metrics = new AgentMetrics();
        const int workers = 16;
        const int perWorker = 5_000;

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                metrics.IncrementMessagesProcessed();
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal((long)workers * perWorker, metrics.Snapshot().TotalMessagesProcessed);
    }

    // ── Token refresh counters ────────────────────────────────────────────

    [Fact]
    public void TokenRefreshCounters_TrackSuccessAndFailureIndependently()
    {
        var metrics = new AgentMetrics();

        metrics.IncrementTokenRefreshSuccess();
        metrics.IncrementTokenRefreshSuccess();
        metrics.IncrementTokenRefreshFailure();

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.TokenRefreshSuccesses);
        Assert.Equal(1, snapshot.TokenRefreshFailures);
    }

    [Fact]
    public async Task TokenRefreshCounters_AreAtomicUnderParallelLoad()
    {
        var metrics = new AgentMetrics();
        const int workers = 8;
        const int perWorker = 10_000;

        var successTasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                metrics.IncrementTokenRefreshSuccess();
            }
        }));
        var failureTasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                metrics.IncrementTokenRefreshFailure();
            }
        }));

        await Task.WhenAll(successTasks.Concat(failureTasks));

        var snapshot = metrics.Snapshot();
        Assert.Equal(workers * perWorker, snapshot.TokenRefreshSuccesses);
        Assert.Equal(workers * perWorker, snapshot.TokenRefreshFailures);
    }

    // ── Queue-depth gauges + peak tracking ────────────────────────────────

    [Fact]
    public void ObserveInboundQueueDepth_RecordsCurrentDepth()
    {
        var metrics = new AgentMetrics();

        metrics.ObserveInboundQueueDepth(5);

        Assert.Equal(5, metrics.Snapshot().InboundQueueDepth);
    }

    [Fact]
    public void ObserveInboundQueueDepth_DepthFollowsLatestObservationUpAndDown()
    {
        var metrics = new AgentMetrics();

        metrics.ObserveInboundQueueDepth(7);
        metrics.ObserveInboundQueueDepth(2);

        // The gauge reflects the most recent observation, including drains.
        Assert.Equal(2, metrics.Snapshot().InboundQueueDepth);
    }

    [Fact]
    public void InboundQueuePeak_HoldsHighWaterMarkAfterDrain()
    {
        var metrics = new AgentMetrics();

        metrics.ObserveInboundQueueDepth(3);
        metrics.ObserveInboundQueueDepth(9);
        metrics.ObserveInboundQueueDepth(1);

        var snapshot = metrics.Snapshot();
        Assert.Equal(1, snapshot.InboundQueueDepth);
        Assert.Equal(9, snapshot.InboundQueuePeak);
    }

    [Fact]
    public void ExtractionQueuePeak_HoldsHighWaterMarkAfterDrain()
    {
        var metrics = new AgentMetrics();

        metrics.ObserveExtractionQueueDepth(4);
        metrics.ObserveExtractionQueueDepth(12);
        metrics.ObserveExtractionQueueDepth(0);

        var snapshot = metrics.Snapshot();
        Assert.Equal(0, snapshot.ExtractionQueueDepth);
        Assert.Equal(12, snapshot.ExtractionQueuePeak);
    }

    [Fact]
    public async Task QueuePeak_CapturesTrueMaximumUnderParallelObservations()
    {
        var metrics = new AgentMetrics();
        const int workers = 16;

        // Each worker observes depths 1..worker*100; the global max is workers*100.
        var tasks = Enumerable.Range(1, workers).Select(maxDepth => Task.Run(() =>
        {
            for (var depth = 1; depth <= maxDepth * 100; depth++)
            {
                metrics.ObserveInboundQueueDepth(depth);
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(workers * 100, metrics.Snapshot().InboundQueuePeak);
    }

    // ── Active conversations provider ─────────────────────────────────────

    [Fact]
    public void Snapshot_ReadsActiveConversationsFromProviderLazily()
    {
        var metrics = new AgentMetrics();
        var live = 0;
        metrics.SetActiveConversationsProvider(() => live);

        Assert.Equal(0, metrics.Snapshot().ActiveConversations);

        live = 4;
        Assert.Equal(4, metrics.Snapshot().ActiveConversations);
    }

    [Fact]
    public void Snapshot_FaultyActiveConversationsProvider_DoesNotThrow()
    {
        var metrics = new AgentMetrics();
        metrics.SetActiveConversationsProvider(() => throw new InvalidOperationException("boom"));

        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.ActiveConversations);
    }

    [Fact]
    public void SetActiveConversationsProvider_Null_ReportsZero()
    {
        var metrics = new AgentMetrics();
        metrics.SetActiveConversationsProvider(() => 9);
        metrics.SetActiveConversationsProvider(null);

        Assert.Equal(0, metrics.Snapshot().ActiveConversations);
    }

    // ── Subagent aggregate provider ─────────────────────────────────────────

    [Fact]
    public void Snapshot_FreshInstance_SubagentsIsNull()
    {
        var metrics = new AgentMetrics();

        Assert.Null(metrics.Snapshot().Subagents);
    }

    [Fact]
    public void Snapshot_ReadsSubagentAggregateFromProviderLazily()
    {
        var metrics = new AgentMetrics();
        var aggregate = new SubagentAggregateSnapshot
        {
            CountsByState = new Dictionary<string, int> { ["running"] = 1 },
            QueueDepth = 0,
            ActiveCount = 1,
            MaxConcurrency = 5,
            StaleActiveCount = 0,
            RestartCount = 0,
        };
        metrics.SetSubagentAggregateProvider(() => aggregate);

        var snapshot = metrics.Snapshot();

        Assert.NotNull(snapshot.Subagents);
        Assert.Equal(1, snapshot.Subagents!.ActiveCount);
    }

    [Fact]
    public void Snapshot_FaultySubagentAggregateProvider_DoesNotThrow_AndDegradesToNull()
    {
        var metrics = new AgentMetrics();
        metrics.SetSubagentAggregateProvider(() => throw new InvalidOperationException("boom"));

        var snapshot = metrics.Snapshot();

        Assert.Null(snapshot.Subagents);
    }

    [Fact]
    public void SetSubagentAggregateProvider_Null_ReportsNull()
    {
        var metrics = new AgentMetrics();
        metrics.SetSubagentAggregateProvider(() => new SubagentAggregateSnapshot
        {
            CountsByState = new Dictionary<string, int>(),
            QueueDepth = 0,
            ActiveCount = 0,
            MaxConcurrency = 5,
            StaleActiveCount = 0,
            RestartCount = 0,
        });
        metrics.SetSubagentAggregateProvider(null);

        Assert.Null(metrics.Snapshot().Subagents);
    }
}
