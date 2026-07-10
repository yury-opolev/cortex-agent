using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DaveDecryptBurstTrackerTests
{
    private static long Sec(double s) => (long)(s * TimeSpan.TicksPerSecond);

    [Fact]
    public void WorstActive_AccumulatesFailures_TracksCountAndAge()
    {
        var t = new DaveDecryptBurstTracker();
        t.RecordFailure(1, "DecryptionFailure", Sec(0));
        t.RecordFailure(1, "DecryptionFailure", Sec(1));
        t.RecordFailure(1, "DecryptionFailure", Sec(2));

        var probe = t.WorstActive(Sec(30));
        Assert.Equal(3, probe.FailuresSinceReset);
        Assert.Equal(Sec(30), probe.TicksSinceFirstFailure);
    }

    [Fact]
    public void WorstActive_NoFailures_IsZero()
    {
        var t = new DaveDecryptBurstTracker();
        var probe = t.WorstActive(Sec(10));
        Assert.Equal(0, probe.FailuresSinceReset);
    }

    [Fact]
    public void Reset_AfterFailures_ReturnsSummary_AndClears()
    {
        var t = new DaveDecryptBurstTracker();
        t.RecordFailure(1, "DecryptionFailure", Sec(0));
        t.RecordFailure(1, "DecryptionFailure", Sec(4));

        var summary = t.Reset(1, Sec(5));
        Assert.NotNull(summary);
        Assert.Equal(1UL, summary!.Value.UserId);
        Assert.Equal(2, summary.Value.FailureCount);
        Assert.Equal(4000, summary.Value.DurationMs);

        Assert.Equal(0, t.WorstActive(Sec(6)).FailuresSinceReset);
    }

    [Fact]
    public void Reset_NoActiveRun_ReturnsNull()
    {
        var t = new DaveDecryptBurstTracker();
        Assert.Null(t.Reset(1, Sec(1)));
    }

    [Fact]
    public void WorstActive_MultipleUsers_ReturnsHighestCount()
    {
        var t = new DaveDecryptBurstTracker();
        t.RecordFailure(1, "DecryptionFailure", Sec(0));
        t.RecordFailure(2, "DecryptionFailure", Sec(0));
        t.RecordFailure(2, "DecryptionFailure", Sec(1));

        Assert.Equal(2, t.WorstActive(Sec(2)).FailuresSinceReset);
    }
}
