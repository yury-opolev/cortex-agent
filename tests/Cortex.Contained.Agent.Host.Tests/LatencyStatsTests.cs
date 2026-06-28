using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Pins the rolling-window latency aggregator behind the per-turn telemetry:
/// avg/p50/p95 over the last N samples, used for the "are we getting faster?"
/// view in /health.
/// </summary>
public class LatencyStatsTests
{
    [Fact]
    public void Empty_ReturnsZeros()
    {
        var stats = new LatencyStats(capacity: 100);

        var s = stats.Snapshot();

        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.AvgMs);
        Assert.Equal(0, s.P50Ms);
        Assert.Equal(0, s.P95Ms);
    }

    [Fact]
    public void OneToTen_ComputesAvgAndPercentiles()
    {
        var stats = new LatencyStats(capacity: 100);
        for (long i = 1; i <= 10; i++)
        {
            stats.Add(i);
        }

        var s = stats.Snapshot();

        Assert.Equal(10, s.Count);
        Assert.Equal(5.5, s.AvgMs, precision: 3);
        Assert.Equal(5, s.P50Ms);   // nearest-rank: ceil(0.50*10)=5th smallest
        Assert.Equal(10, s.P95Ms);  // nearest-rank: ceil(0.95*10)=10th smallest
    }

    [Fact]
    public void SingleSample_AllEqualThatSample()
    {
        var stats = new LatencyStats(capacity: 100);
        stats.Add(42);

        var s = stats.Snapshot();

        Assert.Equal(1, s.Count);
        Assert.Equal(42, s.AvgMs, precision: 3);
        Assert.Equal(42, s.P50Ms);
        Assert.Equal(42, s.P95Ms);
    }

    [Fact]
    public void ExceedsCapacity_KeepsOnlyMostRecent()
    {
        var stats = new LatencyStats(capacity: 3);
        stats.Add(1);
        stats.Add(2);
        stats.Add(3);
        stats.Add(4);
        stats.Add(5); // window now holds [3,4,5]

        var s = stats.Snapshot();

        Assert.Equal(3, s.Count);
        Assert.Equal(4.0, s.AvgMs, precision: 3);
        Assert.Equal(4, s.P50Ms);   // ceil(0.50*3)=2nd of [3,4,5]
        Assert.Equal(5, s.P95Ms);   // ceil(0.95*3)=3rd of [3,4,5]
    }
}
