using Cortex.Contained.Speech;

namespace Cortex.Contained.Channels.Voice;

/// <summary>
/// Utility methods for working with 16kHz mono 16-bit PCM audio data.
/// Thin facade over <see cref="AudioFormat"/> and <see cref="AudioConverter"/>.
/// </summary>
internal static class AudioHelpers
{
    /// <summary>16kHz sample rate required by Whisper.</summary>
    internal const int SampleRate = 16_000;

    /// <summary>Mono channel count.</summary>
    internal const int Channels = 1;

    /// <summary>16-bit = 2 bytes per sample.</summary>
    internal const int BytesPerSample = 2;

    /// <summary>Bytes per second = 16000 * 1 * 2 = 32000.</summary>
    internal const int BytesPerSecond = SampleRate * Channels * BytesPerSample;

    /// <summary>
    /// Compute the Root Mean Square energy of 16-bit PCM audio.
    /// Returns a value in [0.0, 1.0] where 0 = silence.
    /// </summary>
    internal static float ComputeRms(ReadOnlySpan<byte> pcmData)
    {
        return AudioConverter.ComputeRms(pcmData);
    }

    /// <summary>
    /// Convert 16-bit PCM byte array to float samples in [-1.0, 1.0] range
    /// as required by Whisper.net.
    /// </summary>
    internal static float[] PcmToFloat(ReadOnlySpan<byte> pcmData)
    {
        return AudioConverter.Pcm16ToFloat(pcmData);
    }

    /// <summary>
    /// Calculate the number of bytes needed for a given duration at 16kHz mono 16-bit.
    /// </summary>
    internal static int MillisecondsToBytes(int milliseconds)
    {
        return AudioFormat.Whisper.MillisecondsToBytes(milliseconds);
    }
}
