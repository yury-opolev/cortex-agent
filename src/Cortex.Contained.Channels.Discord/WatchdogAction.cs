namespace Cortex.Contained.Channels.Discord;

/// <summary>Action the voice watchdog should take on a single tick.</summary>
public enum WatchdogAction
{
    /// <summary>Do nothing — transport healthy, user absent, or within force cooldown.</summary>
    None = 0,

    /// <summary>
    /// Transport is not alive (<c>ConnectionState != Connected</c>) and the user is present —
    /// re-establish via the normal connect path.
    /// </summary>
    Reconnect,

    /// <summary>
    /// Transport reports alive but is suspected dead (e.g. a silent audio-task cancellation that
    /// left <c>ConnectionState</c> stale at <c>Connected</c>) — force a teardown + rejoin,
    /// bypassing the liveness early-return. Cooldown-guarded against reconnect storms.
    /// </summary>
    ForceReconnect,
}
