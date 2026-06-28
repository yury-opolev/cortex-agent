namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure decision for the per-handler voice watchdog. Extracted so the recovery
/// policy that heals a silent audio-transport death (2026-06-28 outage) is
/// unit-testable without a live Discord client.
/// </summary>
public static class VoiceWatchdogDecision
{
    /// <summary>
    /// Decide the watchdog action for the current tick.
    /// </summary>
    /// <param name="userPresent">Linked user is in the target voice channel.</param>
    /// <param name="isConnected">Transport reports alive (<c>ConnectionState == Connected</c>).</param>
    /// <param name="suspectDead">An audio-death log signal arrived since the last successful (re)connect.</param>
    /// <param name="lastForcedReconnectTicks">Ticks of the last forced reconnect (0 = never).</param>
    /// <param name="nowTicks">Current time in ticks.</param>
    /// <param name="cooldownTicks">Minimum ticks between forced reconnects.</param>
    public static WatchdogAction Decide(
        bool userPresent,
        bool isConnected,
        bool suspectDead,
        long lastForcedReconnectTicks,
        long nowTicks,
        long cooldownTicks)
    {
        // Never touch a connection the user has left — a reconnect would drag the
        // bot back into an empty channel.
        if (!userPresent)
        {
            return WatchdogAction.None;
        }

        // A genuinely dead transport uses the normal (un-cooled) connect path.
        if (!isConnected)
        {
            return WatchdogAction.Reconnect;
        }

        // Transport claims alive. Only act if a death signal says otherwise, and
        // only outside the cooldown so a misclassified signal can't storm-reconnect.
        if (suspectDead && nowTicks - lastForcedReconnectTicks >= cooldownTicks)
        {
            return WatchdogAction.ForceReconnect;
        }

        return WatchdogAction.None;
    }
}
