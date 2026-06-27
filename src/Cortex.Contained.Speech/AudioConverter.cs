using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Cortex.Contained.Speech;

/// <summary>
/// Utility methods for PCM audio conversion and resampling.
/// </summary>
public static class AudioConverter
{
    /// <summary>
    /// Decodes a little-endian PCM16 byte buffer into a freshly allocated
    /// <see cref="short"/> array. The output is endianness-correct on any
    /// platform.
    /// </summary>
    public static ReadOnlyMemory<short> BytesToShorts(ReadOnlySpan<byte> pcm16LeBytes)
    {
        if ((pcm16LeBytes.Length & 1) != 0)
        {
            throw new ArgumentException("PCM16 byte length must be even.", nameof(pcm16LeBytes));
        }

        var samples = new short[pcm16LeBytes.Length / 2];
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, short>(pcm16LeBytes).CopyTo(samples);
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16LeBytes.Slice(i * 2, 2));
            }
        }
        return samples;
    }
    /// <summary>
    /// Applies a linear gain multiplier to a 16-bit little-endian PCM buffer in place, with
    /// saturating clamps so overshoot clips to <see cref="short.MaxValue"/> /
    /// <see cref="short.MinValue"/> instead of wrapping.
    /// </summary>
    /// <remarks>
    /// Gain of 1.0 is a no-op. TTS models often peak well below digital full-scale, which
    /// makes their output softer than typical human voices on Discord; a per-provider
    /// boost of 1.5–2.0 (+3.5 to +6 dB) is usually enough to match levels without clipping.
    /// </remarks>
    public static void ApplyGain(byte[] pcm, float gain)
    {
        if (gain == 1.0f || pcm.Length < 2)
        {
            return;
        }

        var span = pcm.AsSpan();
        var sampleCount = span.Length / 2;
        for (var i = 0; i < sampleCount; i++)
        {
            var offset = i * 2;
            var sample = BinaryPrimitives.ReadInt16LittleEndian(span[offset..]);
            var scaled = sample * gain;
            short clamped = scaled >= short.MaxValue ? short.MaxValue
                          : scaled <= short.MinValue ? short.MinValue
                          : (short)scaled;
            BinaryPrimitives.WriteInt16LittleEndian(span[offset..], clamped);
        }
    }

    /// <summary>
    /// Convert float samples in [-1.0, 1.0] range to 16-bit PCM byte array.
    /// </summary>
    public static byte[] FloatToPcm16(ReadOnlySpan<float> samples)
    {
        var result = new byte[samples.Length * 2];

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var value = (short)(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), value);
        }

        return result;
    }

    /// <summary>
    /// Convert 16-bit PCM byte array to float samples in [-1.0, 1.0] range.
    /// </summary>
    public static float[] Pcm16ToFloat(ReadOnlySpan<byte> pcmData)
    {
        var sampleCount = pcmData.Length / 2;
        var floats = new float[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcmData[(i * 2)..]);
            floats[i] = sample / (float)short.MaxValue;
        }

        return floats;
    }

    /// <summary>
    /// Trim leading and trailing silence from a PCM16 mono buffer using a
    /// fixed-window RMS energy threshold. The returned slice is a view into
    /// <paramref name="pcm"/> (zero-copy on Memory). Window defaults to ~25 ms
    /// at 16 kHz (400 samples). A frame is "voiced" when its RMS exceeds
    /// <paramref name="threshold"/>.
    /// </summary>
    /// <returns>The trimmed segment. Empty when no voiced window is found.</returns>
    public static ReadOnlyMemory<short> TrimSilence(
        ReadOnlyMemory<short> pcm,
        float threshold = 0.01f,
        int windowSamples = 400)
    {
        var span = pcm.Span;
        if (span.Length == 0 || windowSamples <= 0)
        {
            return ReadOnlyMemory<short>.Empty;
        }

        int firstVoiced = -1;
        int lastVoiced = -1;
        for (var start = 0; start + windowSamples <= span.Length; start += windowSamples)
        {
            if (ComputeFrameRms(span.Slice(start, windowSamples)) >= threshold)
            {
                if (firstVoiced < 0)
                {
                    firstVoiced = start;
                }
                lastVoiced = start + windowSamples;
            }
        }

        if (firstVoiced < 0)
        {
            return ReadOnlyMemory<short>.Empty;
        }

        return pcm[firstVoiced..lastVoiced];
    }

    private static float ComputeFrameRms(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0)
        {
            return 0.0f;
        }
        double sumSquares = 0.0;
        for (var i = 0; i < samples.Length; i++)
        {
            var normalized = samples[i] / (double)short.MaxValue;
            sumSquares += normalized * normalized;
        }
        return (float)Math.Sqrt(sumSquares / samples.Length);
    }

    /// <summary>
    /// Compute the Root Mean Square energy of 16-bit PCM audio.
    /// Returns a value in [0.0, 1.0] where 0 = silence.
    /// </summary>
    public static float ComputeRms(ReadOnlySpan<byte> pcmData)
    {
        if (pcmData.Length < 2)
        {
            return 0f;
        }

        var sampleCount = pcmData.Length / 2;
        double sumSquares = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcmData[(i * 2)..]);
            var normalized = sample / (double)short.MaxValue;
            sumSquares += normalized * normalized;
        }

        return (float)Math.Sqrt(sumSquares / sampleCount);
    }

    /// <summary>
    /// Downmix 16-bit stereo (interleaved L R L R ...) PCM to 16-bit mono by averaging each L/R pair.
    /// Output length is exactly half of input length.
    /// </summary>
    /// <param name="stereoPcm">Source 16-bit interleaved stereo PCM data.</param>
    /// <returns>16-bit mono PCM data.</returns>
    public static byte[] StereoToMono(ReadOnlySpan<byte> stereoPcm)
    {
        // Two 16-bit samples (L+R) = 4 bytes per stereo frame; output is 2 bytes per frame.
        var frameCount = stereoPcm.Length / 4;
        var result = new byte[frameCount * 2];

        for (var i = 0; i < frameCount; i++)
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(stereoPcm[(i * 4)..]);
            var right = BinaryPrimitives.ReadInt16LittleEndian(stereoPcm[(i * 4 + 2)..]);
            var mixed = (short)((left + right) / 2);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), mixed);
        }

        return result;
    }

    /// <summary>
    /// Upmix 16-bit mono PCM to 16-bit stereo (interleaved L R L R ...) by duplicating each sample.
    /// Output length is exactly double the input length.
    /// </summary>
    /// <param name="monoPcm">Source 16-bit mono PCM data.</param>
    /// <returns>16-bit interleaved stereo PCM data.</returns>
    public static byte[] MonoToStereo(ReadOnlySpan<byte> monoPcm)
    {
        var sampleCount = monoPcm.Length / 2;
        var result = new byte[sampleCount * 4];

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(monoPcm[(i * 2)..]);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 4 + 2), sample);
        }

        return result;
    }

    /// <summary>
    /// Resample 16-bit mono PCM audio from one sample rate to another using linear interpolation.
    /// </summary>
    /// <param name="pcmData">Source 16-bit mono PCM data.</param>
    /// <param name="sourceSampleRate">Source sample rate in Hz.</param>
    /// <param name="targetSampleRate">Target sample rate in Hz.</param>
    /// <returns>Resampled 16-bit mono PCM data.</returns>
    public static byte[] Resample(ReadOnlySpan<byte> pcmData, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate == targetSampleRate)
        {
            return pcmData.ToArray();
        }

        var sourceSamples = pcmData.Length / 2;
        var targetSamples = (int)((long)sourceSamples * targetSampleRate / sourceSampleRate);
        var result = new byte[targetSamples * 2];
        var ratio = (double)sourceSampleRate / targetSampleRate;

        for (var i = 0; i < targetSamples; i++)
        {
            var srcPos = i * ratio;
            var srcIndex = (int)srcPos;
            var frac = (float)(srcPos - srcIndex);

            short sample0 = BinaryPrimitives.ReadInt16LittleEndian(pcmData[(srcIndex * 2)..]);
            short sample1 = srcIndex + 1 < sourceSamples
                ? BinaryPrimitives.ReadInt16LittleEndian(pcmData[((srcIndex + 1) * 2)..])
                : sample0;

            var interpolated = (short)(sample0 + frac * (sample1 - sample0));
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), interpolated);
        }

        return result;
    }

    /// <summary>
    /// Decode an OGG/Opus audio stream to raw PCM samples.
    /// Returns 48kHz mono 16-bit PCM (Opus native decode rate).
    /// </summary>
    /// <param name="oggData">OGG/Opus file bytes.</param>
    /// <returns>48kHz mono 16-bit PCM audio data.</returns>
    public static byte[] DecodeOggOpus(byte[] oggData)
    {
        ArgumentNullException.ThrowIfNull(oggData);

        using var inputStream = new MemoryStream(oggData);
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(48000, 1);
        var oggIn = new Concentus.Oggfile.OpusOggReadStream(decoder, inputStream);

        var allSamples = new List<byte>();

        while (oggIn.HasNextPacket)
        {
            var samples = oggIn.DecodeNextPacket();
            if (samples is null || samples.Length == 0)
            {
                continue;
            }

            // Convert short[] to byte[]
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
            }

            allSamples.AddRange(bytes);
        }

        return [.. allSamples];
    }

    /// <summary>
    /// Encode raw PCM audio to OGG/Opus format.
    /// </summary>
    /// <param name="pcmData">Raw 16-bit mono PCM data.</param>
    /// <param name="sampleRate">Sample rate of the input PCM data.</param>
    /// <returns>OGG/Opus encoded bytes.</returns>
    public static byte[] EncodeOggOpus(byte[] pcmData, int sampleRate = 48000)
    {
        ArgumentNullException.ThrowIfNull(pcmData);

        using var outputStream = new MemoryStream();
        var encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 64000;

        // OpusOggWriteStream always encodes at 48kHz internally.
        // If input sample rate differs, it resamples automatically.
        var oggOut = new Concentus.Oggfile.OpusOggWriteStream(encoder, outputStream, inputSampleRate: sampleRate, leaveOpen: true);

        // Convert byte[] PCM to short[]
        var sampleCount = pcmData.Length / 2;
        var samples = new short[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcmData.AsSpan(i * 2));
        }

        // Write in chunks — Opus frame size must be one of: 120, 240, 480, 960, 1920, 2880
        var frameSize = sampleRate == 48000 ? 960 : sampleRate == 24000 ? 480 : 960;
        var offset = 0;
        while (offset + frameSize <= sampleCount)
        {
            oggOut.WriteSamples(samples, offset, frameSize);
            offset += frameSize;
        }

        // Write remaining samples (pad with silence)
        if (offset < sampleCount)
        {
            var remaining = sampleCount - offset;
            var paddedFrame = new short[frameSize];
            Array.Copy(samples, offset, paddedFrame, 0, remaining);
            oggOut.WriteSamples(paddedFrame, 0, frameSize);
        }

        oggOut.Finish();
        return outputStream.ToArray();
    }

    /// <summary>
    /// Build a WAV byte array from float samples at the given sample rate (mono 16-bit).
    /// </summary>
    internal static byte[] BuildWavBytes(float[] samples, int sampleRate)
    {
        var dataSize = samples.Length * 2;
        var wavBytes = new byte[dataSize + 44];

        WriteWavHeader(wavBytes, dataSize, sampleRate);

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var value = (short)(clamped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wavBytes.AsSpan(44 + i * 2), value);
        }

        return wavBytes;
    }

    /// <summary>
    /// Write a standard WAV header for mono 16-bit PCM.
    /// </summary>
    internal static void WriteWavHeader(byte[] buffer, int dataSize, int sampleRate)
    {
        var totalSize = dataSize + 36;
        var byteRate = sampleRate * 2; // mono, 16-bit

        // RIFF header
        buffer[0] = (byte)'R'; buffer[1] = (byte)'I'; buffer[2] = (byte)'F'; buffer[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), totalSize);
        buffer[8] = (byte)'W'; buffer[9] = (byte)'A'; buffer[10] = (byte)'V'; buffer[11] = (byte)'E';

        // fmt sub-chunk
        buffer[12] = (byte)'f'; buffer[13] = (byte)'m'; buffer[14] = (byte)'t'; buffer[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 16); // Sub-chunk size
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(20), 1); // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(22), 1); // Mono
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(28), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(32), 2); // Block align
        BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(34), 16); // Bits per sample

        // data sub-chunk
        buffer[36] = (byte)'d'; buffer[37] = (byte)'a'; buffer[38] = (byte)'t'; buffer[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(40), dataSize);
    }
}
