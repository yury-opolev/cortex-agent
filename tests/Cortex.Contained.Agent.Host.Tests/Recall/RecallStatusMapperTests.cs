using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Recall;
using Cortex.Contained.Agent.Host.Reranking;
using MemoryMcp.Core.Configuration;

namespace Cortex.Contained.Agent.Host.Tests.Recall;

public sealed class RecallStatusMapperTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Settings_ReflectsEffectiveOptionsAndOverrideNames()
    {
        var history = new HistoryOptions { Enabled = true, DefaultSearchMode = HistorySearchMode.Hybrid };
        history.Dense.Enabled = true;
        var reranker = new RerankerOptions { TimeoutMs = 2500 };
        reranker.History.TopN = 12;

        var settings = RecallStatusMapper.Settings(history, reranker, new MemorySearchOptions { DefaultMode = MemorySearchMode.Keyword }, new RecallOverrides { HistoryEnabled = true });

        Assert.True(settings.HistoryEnabled);
        Assert.True(settings.HistoryDenseEnabled);
        Assert.Equal("hybrid", settings.HistorySearchMode);
        Assert.Equal("keyword", settings.MemorySearchMode);
        Assert.Equal(12, settings.RerankHistoryTopN);
        Assert.Equal(1200, settings.RerankMemoryMaxDocChars);
        Assert.Equal(2500, settings.RerankTimeoutMs);
        Assert.Equal(["historyEnabled"], settings.Overridden);
    }

    [Fact]
    public void Status_MapsEverySubsystem()
    {
        var settings = RecallStatusMapper.Settings(new HistoryOptions(), new RerankerOptions(), new MemorySearchOptions(), RecallOverrides.None);
        var history = new HistoryHealth(true, true, "3.51.2", 3, false, true, 4, 9, false, null, 1, ["index"], 0)
        {
            Dense = new HistoryDenseHealth("on", 40, 100, 60, 1, T0, null),
        };
        var reranker = new RerankHealth(true, "circuit-open", 3, T0.AddMinutes(1), "timed out after 2000 ms", 2100, T0);
        var backfill = new EventBackfillStatus(false, new EventBackfillReport(10, 4, TimeSpan.FromMilliseconds(250)), T0);

        var status = RecallStatusMapper.Status(settings, history, reranker, backfill, quarantinedMemories: 2);

        Assert.Equal(3, status.History.Generation);
        Assert.True(status.History.BuildInProgress);
        Assert.Equal(4, status.History.CheckpointLag);
        Assert.Equal(["index"], status.History.PendingErasureSteps);
        Assert.Equal(60, status.Dense.Pending);
        Assert.Equal("circuit-open", status.Reranker.State);
        Assert.Equal(2100, status.Reranker.LastLatencyMs);
        Assert.Equal(4, status.EventBackfill.LastInserted);
        Assert.Equal(250, status.EventBackfill.LastElapsedMs);
        Assert.Equal(2, status.QuarantinedMemories);
    }

    [Fact]
    public void Status_NoBackfillService_ReportsNeverRun()
    {
        var settings = RecallStatusMapper.Settings(new HistoryOptions(), new RerankerOptions(), new MemorySearchOptions(), RecallOverrides.None);
        var history = new HistoryHealth(false, false, "3.51.2", null, false, false, 0, 0, false, null, 0, [], 0);

        var status = RecallStatusMapper.Status(settings, history, new RerankHealth(false, "off", 0, null, null, null, null), null, 0);

        Assert.False(status.EventBackfill.Running);
        Assert.Null(status.EventBackfill.LastRunAt);
        Assert.Equal("off", status.Dense.State);
    }
}
