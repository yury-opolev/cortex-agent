namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Configuration for one voice-out session. All values are immutable for the
/// session's lifetime; create a new pipeline (with new options if needed) per join.
/// </summary>
internal sealed record VoiceOutOptions
{
    /// <summary>Maximum chars per TTS batch call. See SentenceChunker for the splitting algorithm.</summary>
    public int MaxChunkChars { get; init; } = Cortex.Contained.Speech.SentenceChunker.DefaultMaxChunkChars;

    /// <summary>Linear gain multiplier applied to PCM after stereo upmix.</summary>
    public float OutputGain { get; init; } = 1.0f;

    /// <summary>Sample rate of the TTS engine's PCM output. Resampled to 48 kHz for Discord.</summary>
    public int SourceSampleRate { get; init; } = 48000;
}
