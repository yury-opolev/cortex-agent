using Cortex.Contained.Channels.CloudMessaging.Reconnect;

namespace Cortex.Contained.Channels.CloudMessaging.Tests.Reconnect;

/// <summary>
/// Pins the reconnect backoff delay computation. Uses a zero jitter for
/// determinism.
/// </summary>
public class BackoffDecisionTests
{
    private const int Base = 1_000;
    private const int Max = 60_000;

    [Fact]
    public void AttemptZero_ReturnsBaseDelay()
    {
        var delay = BackoffDecision.ComputeDelay(0, Base, Max, jitterMs: 0, randomJitter: 0.0);

        Assert.Equal(TimeSpan.FromMilliseconds(1_000), delay);
    }

    [Fact]
    public void AttemptOne_ReturnsDoubleBase()
    {
        var delay = BackoffDecision.ComputeDelay(1, Base, Max, jitterMs: 0, randomJitter: 0.0);

        Assert.Equal(TimeSpan.FromMilliseconds(2_000), delay);
    }

    [Fact]
    public void AttemptTwo_ReturnsQuadrupleBase()
    {
        var delay = BackoffDecision.ComputeDelay(2, Base, Max, jitterMs: 0, randomJitter: 0.0);

        Assert.Equal(TimeSpan.FromMilliseconds(4_000), delay);
    }

    [Fact]
    public void HighAttemptCount_ClampedAtMax()
    {
        var delay = BackoffDecision.ComputeDelay(100, Base, Max, jitterMs: 0, randomJitter: 0.0);

        Assert.Equal(TimeSpan.FromMilliseconds(60_000), delay);
    }

    [Fact]
    public void JitterIsAdded()
    {
        var withoutJitter = BackoffDecision.ComputeDelay(0, Base, Max, jitterMs: 1_000, randomJitter: 0.0);
        var withJitter = BackoffDecision.ComputeDelay(0, Base, Max, jitterMs: 1_000, randomJitter: 0.5);

        Assert.True(withJitter > withoutJitter);
        Assert.Equal(withoutJitter + TimeSpan.FromMilliseconds(500), withJitter);
    }

    [Fact]
    public void NegativeAttempt_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BackoffDecision.ComputeDelay(-1));
    }

    [Fact]
    public void ShouldRetry_AlwaysTrue()
    {
        Assert.True(BackoffDecision.ShouldRetry());
    }
}
