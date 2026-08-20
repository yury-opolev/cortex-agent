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
    public void NeverJoined_DoesNotArm()
    {
        Assert.False(DaveMlsRecoveryPolicy.ShouldArm(everJoined: false, ticksSinceJoin: 0, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void FailureShortlyAfterJoin_Arms()
    {
        // The exact outage shape: user joined ~3s after the bot, MLS add fails.
        Assert.True(DaveMlsRecoveryPolicy.ShouldArm(
            everJoined: true, ticksSinceJoin: 3 * TimeSpan.TicksPerSecond, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void FailureAtWindowBoundary_Arms()
    {
        Assert.True(DaveMlsRecoveryPolicy.ShouldArm(
            everJoined: true, ticksSinceJoin: Window, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void FailureLongAfterJoin_DoesNotArm()
    {
        // Mid-session MLS proposal (e.g. another member churns 5 min later) —
        // normal epoch handling, not a join-race wedge. Must not reconnect.
        Assert.False(DaveMlsRecoveryPolicy.ShouldArm(
            everJoined: true, ticksSinceJoin: 5 * 60 * TimeSpan.TicksPerSecond, joinRaceWindowTicks: Window));
    }

    [Fact]
    public void NegativeSinceJoin_DoesNotArm()
    {
        // Clock skew / racing the join stamp — never act on a negative age.
        Assert.False(DaveMlsRecoveryPolicy.ShouldArm(
            everJoined: true, ticksSinceJoin: -1, joinRaceWindowTicks: Window));
    }

    // ── Stage 2: did the armed failure fail to heal itself? ──────────────────

    private static readonly long Settle = 5 * TimeSpan.TicksPerSecond;
    private const long Armed = 1_000_000_000L;

    [Fact]
    public void NotArmed_DoesNotRecover()
    {
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: 0, sessionReadyTicks: 0, nowTicks: Armed + Settle, settleWindowTicks: Settle));
    }

    [Fact]
    public void SessionReadyAfterFailure_DoesNotRecover()
    {
        // THE 2026-08-19 REGRESSION. The MLS add-proposal failed, but Discord's welcome
        // landed 57ms later, the group was established and the bot decrypted the user's
        // audio. The old code rejoined anyway ~950ms later and destroyed a healthy
        // session, wedging voice for 100 minutes. A ready milestone after the failure
        // must disarm it — even long after the settle window has passed.
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: Armed,
            sessionReadyTicks: Armed + (57 * TimeSpan.TicksPerMillisecond),
            nowTicks: Armed + (10 * TimeSpan.TicksPerSecond),
            settleWindowTicks: Settle));
    }

    [Fact]
    public void SessionReadyBeforeFailure_StillRecovers()
    {
        // A ready milestone from an *earlier* epoch proves nothing about this failure.
        Assert.True(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: Armed,
            sessionReadyTicks: Armed - TimeSpan.TicksPerSecond,
            nowTicks: Armed + Settle,
            settleWindowTicks: Settle));
    }

    [Fact]
    public void NeverReady_WithinSettleWindow_DoesNotRecoverYet()
    {
        // The welcome may still be in flight — acting now risks destroying a session
        // that is about to become healthy. This is precisely what the old code did.
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: Armed,
            sessionReadyTicks: 0,
            nowTicks: Armed + (950 * TimeSpan.TicksPerMillisecond),
            settleWindowTicks: Settle));
    }

    [Fact]
    public void NeverReady_PastSettleWindow_Recovers()
    {
        // The genuine 2026-06-29 shape: the group never establishes, so the listener
        // can never decrypt our audio. This is the case the rejoin exists for.
        Assert.True(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: Armed,
            sessionReadyTicks: 0,
            nowTicks: Armed + Settle,
            settleWindowTicks: Settle));
    }

    [Fact]
    public void NegativeAge_DoesNotRecover()
    {
        Assert.False(DaveMlsRecoveryPolicy.ShouldRecover(
            armedTicks: Armed, sessionReadyTicks: 0, nowTicks: Armed - 1, settleWindowTicks: Settle));
    }
}
