namespace Cortex.Contained.Speech;

/// <summary>
/// Describes the format of raw PCM audio data.
/// </summary>
/// <param name="SampleRate">Samples per second (e.g. 16000, 24000, 48000).</param>
/// <param name="Channels">Number of audio channels (1 = mono, 2 = stereo).</param>
/// <param name="BitsPerSample">Bits per sample (16 for 16-bit PCM).</param>
public readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample)
{
    /// <summary>Bytes per single sample across all channels.</summary>
    public int BytesPerSample => BitsPerSample / 8;

    /// <summary>Block align = channels * bytes-per-sample.</summary>
    public int BlockAlign => Channels * BytesPerSample;

    /// <summary>Bytes per second = SampleRate * BlockAlign.</summary>
    public int BytesPerSecond => SampleRate * BlockAlign;

    /// <summary>16kHz mono 16-bit — standard for Whisper STT input.</summary>
    public static readonly AudioFormat Whisper = new(16_000, 1, 16);

    /// <summary>24kHz mono 16-bit — native output of Kokoro TTS.</summary>
    public static readonly AudioFormat Kokoro = new(24_000, 1, 16);

    /// <summary>48kHz mono 16-bit — native output of Silero TTS v5.</summary>
    public static readonly AudioFormat Silero = new(48_000, 1, 16);

    /// <summary>48kHz mono 16-bit — Discord voice message format (after Opus decode).</summary>
    public static readonly AudioFormat Discord = new(48_000, 1, 16);

    /// <summary>
    /// Calculate how many bytes represent the given duration at this format's rate.
    /// </summary>
    public int MillisecondsToBytes(int milliseconds) => BytesPerSecond * milliseconds / 1000;
}
