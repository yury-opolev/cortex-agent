using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DaveDecryptFloodPolicyTests
{
    private const long Threshold = 50;
    private static readonly long Window = TimeSpan.FromSeconds(30).Ticks;

    [Fact]
    public void ShouldRecover_SustainedFloodNoCommitUserPresent_Trips()
    {
        Assert.True(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 200,
            ticksSinceFirstFailure: Window + 1, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_Silence_NoFailures_DoesNotTrip()
    {
        // No decrypt failures at all — user simply not talking.
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 0,
            ticksSinceFirstFailure: 0, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_BelowThreshold_DoesNotTrip()
    {
        // A brief transient burst (e.g. normal epoch churn) under the threshold.
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 10,
            ticksSinceFirstFailure: Window + 1, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_FloodButWithinWindow_DoesNotTripYet()
    {
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: true, failuresSinceCommit: 200,
            ticksSinceFirstFailure: Window - 1, Threshold, Window));
    }

    [Fact]
    public void ShouldRecover_UserAbsent_DoesNotTrip()
    {
        Assert.False(DaveDecryptFloodPolicy.ShouldRecover(
            userPresent: false, failuresSinceCommit: 200,
            ticksSinceFirstFailure: Window + 1, Threshold, Window));
    }
}
