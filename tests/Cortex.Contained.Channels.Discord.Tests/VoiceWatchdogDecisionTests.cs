using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Pins the pure decision the per-handler voice watchdog makes on each tick.
/// Recovery must be aggressive enough to heal a silent audio-transport death
/// within one tick, but the forced-reconnect path (used when ConnectionState
/// lies) must be cooldown-guarded so a misfire can never cause a reconnect storm.
/// </summary>
public class VoiceWatchdogDecisionTests
{
    private const long Cooldown = 25 * TimeSpan.TicksPerSecond;
    private static readonly long Now = TimeSpan.FromHours(100).Ticks; // any large "now"

    [Fact]
    public void UserAbsent_NeverActs()
    {
        Assert.Equal(
            WatchdogAction.None,
            VoiceWatchdogDecision.Decide(userPresent: false, isConnected: false, suspectDead: true,
                lastForcedReconnectTicks: 0, nowTicks: Now, cooldownTicks: Cooldown));
    }

    [Fact]
    public void PresentAndTransportNotAlive_Reconnects()
    {
        Assert.Equal(
            WatchdogAction.Reconnect,
            VoiceWatchdogDecision.Decide(userPresent: true, isConnected: false, suspectDead: false,
                lastForcedReconnectTicks: 0, nowTicks: Now, cooldownTicks: Cooldown));
    }

    [Fact]
    public void PresentAndAliveAndHealthy_DoesNothing()
    {
        Assert.Equal(
            WatchdogAction.None,
            VoiceWatchdogDecision.Decide(userPresent: true, isConnected: true, suspectDead: false,
                lastForcedReconnectTicks: 0, nowTicks: Now, cooldownTicks: Cooldown));
    }

    [Fact]
    public void PresentAndStaleConnectedButSuspectDead_ForcesReconnect_WhenCooldownElapsed()
    {
        // lastForced far in the past → cooldown elapsed → force allowed
        Assert.Equal(
            WatchdogAction.ForceReconnect,
            VoiceWatchdogDecision.Decide(userPresent: true, isConnected: true, suspectDead: true,
                lastForcedReconnectTicks: Now - Cooldown, nowTicks: Now, cooldownTicks: Cooldown));
    }

    [Fact]
    public void PresentAndSuspectDead_WithinCooldown_DoesNotForce()
    {
        // forced 1s ago, cooldown 25s → must hold
        Assert.Equal(
            WatchdogAction.None,
            VoiceWatchdogDecision.Decide(userPresent: true, isConnected: true, suspectDead: true,
                lastForcedReconnectTicks: Now - TimeSpan.TicksPerSecond, nowTicks: Now, cooldownTicks: Cooldown));
    }

    [Fact]
    public void NotAliveTakesPrecedenceOverSuspectDead_PlainReconnect()
    {
        // A real dead state uses the plain (un-cooled) reconnect path, not force.
        Assert.Equal(
            WatchdogAction.Reconnect,
            VoiceWatchdogDecision.Decide(userPresent: true, isConnected: false, suspectDead: true,
                lastForcedReconnectTicks: Now - TimeSpan.TicksPerSecond, nowTicks: Now, cooldownTicks: Cooldown));
    }
}
