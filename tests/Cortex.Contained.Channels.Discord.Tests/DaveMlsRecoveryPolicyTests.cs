using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Pins the rule that turns a DAVE <c>MLS Failure</c> log line into a forced
/// rejoin. The fix heals the 2026-06-29 silent-voice outage: an MLS add-proposal
/// failure during the join race wedges the encrypted group so the listener can't
/// decrypt the bot's audio. We rejoin only when the failure lands in the
/// post-join window — later epoch churn must not thrash the connection.
/// </summary>
public class DaveMlsRecoveryPolicyTests
{
    private static readonly long Window = 20 * TimeSpan.TicksPerSecond;

    [Fact]
    public void NeverJoined_DoesNotRecover()
    {
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(everJoined: false, ticksSinceJoin: 0, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void FailureShortlyAfterJoin_Recovers()
    {
        // The exact outage shape: user joined ~3s after the bot, MLS add fails.
        Assert.True(DaveMlsRecoveryPolicy.ShouldRecover(
            everJoined: true, ticksSinceJoin: 3 * TimeSpan.TicksPerSecond, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void FailureAtWindowBoundary_Recovers()
    {
        Assert.True(DaveMlsRecoveryPolicy.ShouldRecover(
            everJoined: true, ticksSinceJoin: Window, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void FailureLongAfterJoin_DoesNotRecover()
    {
        // Mid-session MLS proposal (e.g. another member churns 5 min later) —
        // normal epoch handling, not a join-race wedge. Must not reconnect.
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            everJoined: true, ticksSinceJoin: 5 * 60 * TimeSpan.TicksPerSecond, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void NegativeSinceJoin_DoesNotRecover()
    {
        // Clock skew / racing the join stamp — never act on a negative age.
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            everJoined: true, ticksSinceJoin: -1, joinRaceWindowTicks: Window));
    }
}
