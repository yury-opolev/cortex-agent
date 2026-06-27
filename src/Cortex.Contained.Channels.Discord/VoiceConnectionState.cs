using Discord;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure predicate for "is the bot's voice transport actually live?". Extracted
/// so the rule that broke Discord voice on 2026-05-15 is unit-testable: a
/// gateway reconnect leaves a non-null <see cref="Discord.Audio.IAudioClient"/>
/// whose <see cref="ConnectionState"/> is no longer <see cref="ConnectionState.Connected"/>.
/// Only <see cref="ConnectionState.Connected"/> counts as alive — a null state
/// (no client) or any transitional/dead state does not.
/// </summary>
internal static class VoiceConnectionState
{
    public static bool IsAlive(ConnectionState? state)
        => state == ConnectionState.Connected;
}
