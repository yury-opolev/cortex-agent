using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Pins the rule that heals the 2026-08-19 outage: a DAVE handshake that started
/// (key package sent) but never reached its completion milestone leaves the bot
/// permanently deaf while the transport still reports Connected and every DAVE
/// failure counter stays at zero.
/// </summary>
public class DaveHandshakeStallPolicyTests
{
    private static readonly long Window = 30 * TimeSpan.TicksPerSecond;
    private const long Started = 1_000_000_000L;

    [Fact]
    public void HandshakeNeverStarted_IsNotUnhealed()
    {
        // DAVE disabled / not initialised — there is no handshake to stall on, so the
        // watchdog must stay completely out of the way.
        Assert.False(DaveHandshakeStallPolicy.IsUnhealed(handshakeStartedTicks: 0, sessionReadyTicks: 0));
    }

    [Fact]
    public void StartedAndNeverReady_IsUnhealed()
    {
        // The wedged Audio #7 shape: init + key package sent, external sender received,
        // then no proposals, no welcome, no transition — ever.
        Assert.True(DaveHandshakeStallPolicy.IsUnhealed(handshakeStartedTicks: Started, sessionReadyTicks: 0));
    }

    [Fact]
    public void ReadyAfterStart_IsHealed()
    {
        Assert.False(DaveHandshakeStallPolicy.IsUnhealed(
            handshakeStartedTicks: Started,
            sessionReadyTicks: Started + (83 * TimeSpan.TicksPerMillisecond)));
    }

    [Fact]
    public void ReadyFromPreviousSession_IsStillUnhealed()
    {
        // A stale ready stamp from the session before this one must not mask a fresh
        // handshake that is going nowhere.
        Assert.True(DaveHandshakeStallPolicy.IsUnhealed(
            handshakeStartedTicks: Started,
            sessionReadyTicks: Started - TimeSpan.TicksPerSecond));
    }

    [Fact]
    public void NotStalling_DoesNotRecover()
    {
        Assert.False(DaveHandshakeStallPolicy.ShouldRecover(
            stallSinceTicks: 0, nowTicks: Started + Window, stallWindowTicks: Window));
    }

    [Fact]
    public void WithinStallWindow_DoesNotRecover()
    {
        // Give a slow-but-progressing handshake room to finish.
        Assert.False(DaveHandshakeStallPolicy.ShouldRecover(
            stallSinceTicks: Started,
            nowTicks: Started + (29 * TimeSpan.TicksPerSecond),
            stallWindowTicks: Window));
    }

    [Fact]
    public void PastStallWindow_Recovers()
    {
        Assert.True(DaveHandshakeStallPolicy.ShouldRecover(
            stallSinceTicks: Started,
            nowTicks: Started + Window,
            stallWindowTicks: Window));
    }

    [Fact]
    public void NegativeAge_DoesNotRecover()
    {
        Assert.False(DaveHandshakeStallPolicy.ShouldRecover(
            stallSinceTicks: Started, nowTicks: Started - 1, stallWindowTicks: Window));
    }
}
