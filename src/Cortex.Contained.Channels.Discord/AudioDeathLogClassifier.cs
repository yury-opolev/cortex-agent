namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure predicate that recognises a silent Discord <em>audio-transport</em> death
/// from a <c>Discord.Net</c> diagnostic log line, so the voice watchdog can react
/// even when no <see cref="Discord.Audio.IAudioClient.Disconnected"/> event fires.
/// <para>
/// The 2026-06-28 outage was exactly this: an <c>"Audio #2: A task was canceled"</c>
/// that raised no Disconnected event and left <c>ConnectionState</c> stale at
/// <c>Connected</c>, so none of the existing event-driven recovery triggers fired
/// and voice stayed dead for ~14 minutes until a manual rejoin.
/// </para>
/// <para>
/// Only the <c>Audio</c>/<c>Voice</c> pipeline sources count — a cancellation on the
/// <c>Gateway</c> source has its own reconnect path (gateway reconnect → voice nudge)
/// and must not be treated as an audio-transport death here.
/// </para>
/// </summary>
public static class AudioDeathLogClassifier
{
    /// <summary>
    /// True when <paramref name="source"/> is the audio/voice pipeline and
    /// <paramref name="message"/> indicates the transport task was canceled
    /// (the silent-death signature). Tolerant of the connection-number suffix
    /// (<c>"Audio #4"</c>) and both spellings of "canceled".
    /// </summary>
    public static bool IsAudioTransportDeath(string? source, string? message)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(message))
        {
            return false;
        }

        var isAudioPipeline =
            source.StartsWith("Audio", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("Voice", StringComparison.OrdinalIgnoreCase);

        if (!isAudioPipeline)
        {
            return false;
        }

        return message.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
            || message.Contains("task was cancelled", StringComparison.OrdinalIgnoreCase);
    }
}
