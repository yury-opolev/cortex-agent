namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure policy mapping a Discord voice-state update to a handler action.
/// Extracted from <see cref="DiscordVoiceHandler"/> so the join / leave /
/// recover routing is unit-testable independent of Discord / audio.
/// </summary>
/// <remarks>
/// Key invariant: a connection is "alive" only when the audio transport is
/// actually <c>Connected</c> — not merely that an
/// <see cref="Discord.Audio.IAudioClient"/> reference exists. A stale reference
/// left behind by a gateway reconnect (the 2026-05-15 voice outage) reports
/// <c>connectionAlive=false</c> here, so a user join correctly routes to a
/// rejoin instead of being silently treated as "already connected".
/// </remarks>
internal static class VoiceStateRouter
{
    /// <param name="joinedTarget">The linked user is now in the target voice channel.</param>
    /// <param name="leftTarget">The linked user just left the target voice channel.</param>
    /// <param name="connectionAlive">The bot's audio transport is actually connected
    /// (not null, not a stale post-reconnect reference).</param>
    /// <param name="otherNonBotUsersPresent">Other non-bot users remain in the
    /// channel the user just left.</param>
    public static VoiceStateAction Route(
        bool joinedTarget,
        bool leftTarget,
        bool connectionAlive,
        bool otherNonBotUsersPresent)
    {
        if (joinedTarget)
        {
            return connectionAlive
                ? VoiceStateAction.DrainProactive
                : VoiceStateAction.JoinAndDrainProactive;
        }

        if (leftTarget && connectionAlive && !otherNonBotUsersPresent)
        {
            return VoiceStateAction.Leave;
        }

        return VoiceStateAction.None;
    }
}
