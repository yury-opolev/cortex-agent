using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryPurgeTypesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_ClustersAndCapsWindows()
    {
        var windows = QuarantineWindowBuilder.Build([T0, T0.AddHours(1), T0.AddHours(30)], TimeSpan.FromHours(6), TimeSpan.FromHours(24), T0.AddHours(40));

        Assert.Equal([new QuarantineWindow(T0, T0.AddHours(25)), new QuarantineWindow(T0.AddHours(30), T0.AddHours(40))], windows);
    }

    [Fact]
    public void Build_NoTimestamps_NoWindows()
    {
        Assert.Empty(QuarantineWindowBuilder.Build([], TimeSpan.FromHours(6), TimeSpan.FromHours(24), T0));
    }

    [Fact]
    public void Contains_IsHalfOpen()
    {
        var window = new QuarantineWindow(T0, T0.AddHours(1));

        Assert.True(window.Contains(T0));
        Assert.False(window.Contains(T0.AddHours(1)));
    }

    [Fact]
    public void ForPurge_ChannelScope_SkipsToolOutputButAllScopeIncludesIt()
    {
        Assert.DoesNotContain(ErasureSteps.ToolOutput, ErasureSteps.ForPurge(new HistoryPurgeScope("ch", null)));
        Assert.Equal(
            [ErasureSteps.Index, ErasureSteps.MessagesWal, ErasureSteps.ToolOutput, ErasureSteps.Subagents, ErasureSteps.SmokeArtifacts, ErasureSteps.Vacuum],
            ErasureSteps.ForPurge(HistoryPurgeScope.All));
    }

    [Fact]
    public void WalCheckpointResult_BusyIsIncomplete()
    {
        Assert.True(new WalCheckpointResult(0, 0, 0).Complete);
        Assert.False(new WalCheckpointResult(1, 5, 3).Complete);
    }
}
