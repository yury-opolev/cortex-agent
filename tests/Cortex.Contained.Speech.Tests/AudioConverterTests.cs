using System.Buffers.Binary;

namespace Cortex.Contained.Speech.Tests;

public class AudioConverterTests
{
    #region FloatToPcm16

    [Fact]
    public void FloatToPcm16_EmptyInput_ReturnsEmptyArray()
    {
        var result = AudioConverter.FloatToPcm16(ReadOnlySpan<float>.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void FloatToPcm16_Silence_ReturnsAllZeros()
    {
        var samples = new float[] { 0f, 0f, 0f };

        var result = AudioConverter.FloatToPcm16(samples);

        Assert.Equal(6, result.Length);
        Assert.All(result, b => Assert.Equal(0, b));
    }

    [Fact]
    public void FloatToPcm16_MaxPositive_ReturnsShortMaxValue()
    {
        var samples = new float[] { 1.0f };

        var result = AudioConverter.FloatToPcm16(samples);

        Assert.Equal(2, result.Length);
        var value = BinaryPrimitives.ReadInt16LittleEndian(result);
        Assert.Equal(short.MaxValue, value);
    }

    [Fact]
    public void FloatToPcm16_MaxNegative_ReturnsNegativeShortMaxValue()
    {
        var samples = new float[] { -1.0f };

        var result = AudioConverter.FloatToPcm16(samples);

        var value = BinaryPrimitives.ReadInt16LittleEndian(result);
        Assert.Equal(-short.MaxValue, value);
    }

    [Fact]
    public void FloatToPcm16_ClampsAboveOne()
    {
        var samples = new float[] { 2.0f };

        var result = AudioConverter.FloatToPcm16(samples);

        var value = BinaryPrimitives.ReadInt16LittleEndian(result);
        Assert.Equal(short.MaxValue, value);
    }

    [Fact]
    public void FloatToPcm16_ClampsBelowNegativeOne()
    {
        var samples = new float[] { -5.0f };

        var result = AudioConverter.FloatToPcm16(samples);

        var value = BinaryPrimitives.ReadInt16LittleEndian(result);
        Assert.Equal(-short.MaxValue, value);
    }

    [Fact]
    public void FloatToPcm16_HalfAmplitude_ReturnsApproximateHalfMax()
    {
        var samples = new float[] { 0.5f };

        var result = AudioConverter.FloatToPcm16(samples);

        var value = BinaryPrimitives.ReadInt16LittleEndian(result);
        Assert.InRange(value, 16000, 16500);
    }

    #endregion

    #region Pcm16ToFloat

    [Fact]
    public void Pcm16ToFloat_EmptyInput_ReturnsEmptyArray()
    {
        var result = AudioConverter.Pcm16ToFloat(ReadOnlySpan<byte>.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void Pcm16ToFloat_Silence_ReturnsAllZeros()
    {
        var silence = new byte[8]; // 4 samples

        var result = AudioConverter.Pcm16ToFloat(silence);

        Assert.Equal(4, result.Length);
        Assert.All(result, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void Pcm16ToFloat_MaxPositive_ReturnsOne()
    {
        // short.MaxValue = 32767 = 0x7FFF little-endian
        var data = new byte[] { 0xFF, 0x7F };

        var result = AudioConverter.Pcm16ToFloat(data);

        Assert.Single(result);
        Assert.Equal(1.0f, result[0], 0.001f);
    }

    [Fact]
    public void Pcm16ToFloat_MaxNegative_ReturnsApproxNegativeOne()
    {
        // short.MinValue = -32768 = 0x8000 little-endian: 0x00, 0x80
        var data = new byte[] { 0x00, 0x80 };

        var result = AudioConverter.Pcm16ToFloat(data);

        Assert.Single(result);
        Assert.True(result[0] < -0.99f);
    }

    [Fact]
    public void Pcm16ToFloat_MultipleSamples_ReturnsCorrectCount()
    {
        var data = new byte[20]; // 10 samples

        var result = AudioConverter.Pcm16ToFloat(data);

        Assert.Equal(10, result.Length);
    }

    #endregion

    #region FloatToPcm16 and Pcm16ToFloat round-trip

    [Fact]
    public void FloatToPcm16_Pcm16ToFloat_RoundTrip()
    {
        var original = new float[] { 0.0f, 0.5f, -0.5f, 0.25f, -0.75f };

        var pcm = AudioConverter.FloatToPcm16(original);
        var roundTripped = AudioConverter.Pcm16ToFloat(pcm);

        Assert.Equal(original.Length, roundTripped.Length);
        for (var i = 0; i < original.Length; i++)
        {
            // 16-bit quantization introduces small error
            Assert.Equal(original[i], roundTripped[i], 0.001f);
        }
    }

    #endregion

    #region ComputeRms

    [Fact]
    public void ComputeRms_EmptyArray_ReturnsZero()
    {
        var result = AudioConverter.ComputeRms(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ComputeRms_SingleByte_ReturnsZero()
    {
        var result = AudioConverter.ComputeRms(new byte[] { 0xFF });

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ComputeRms_AllZeros_ReturnsZero()
    {
        var silence = new byte[100];

        var result = AudioConverter.ComputeRms(silence);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ComputeRms_MaxAmplitude_ReturnsOne()
    {
        // short.MaxValue = 32767 = 0x7FFF little-endian
        var data = new byte[] { 0xFF, 0x7F };

        var result = AudioConverter.ComputeRms(data);

        Assert.Equal(1.0f, result, 0.001f);
    }

    [Fact]
    public void ComputeRms_MixedSamples_ReturnsExpectedValue()
    {
        // +16383 = 0x3FFF, -16383 = 0xC001 (signed)
        var data = new byte[] { 0xFF, 0x3F, 0x01, 0xC0 };

        var result = AudioConverter.ComputeRms(data);

        Assert.True(result > 0.4f && result < 0.6f);
    }

    #endregion

    #region Resample

    [Fact]
    public void Resample_SameRate_ReturnsCopy()
    {
        var data = new byte[] { 0xFF, 0x7F, 0x00, 0x00 };

        var result = AudioConverter.Resample(data, 16000, 16000);

        Assert.Equal(data, result);
    }

    [Fact]
    public void Resample_Upsample_DoublesLength()
    {
        // 4 bytes = 2 samples at 16kHz -> 4 samples at 32kHz = 8 bytes
        var data = new byte[] { 0x00, 0x40, 0x00, 0x40 };

        var result = AudioConverter.Resample(data, 16000, 32000);

        Assert.Equal(8, result.Length);
    }

    [Fact]
    public void Resample_Downsample_HalvesLength()
    {
        // 8 bytes = 4 samples at 32kHz -> 2 samples at 16kHz = 4 bytes
        var data = new byte[] { 0x00, 0x40, 0x00, 0x40, 0x00, 0x40, 0x00, 0x40 };

        var result = AudioConverter.Resample(data, 32000, 16000);

        Assert.Equal(4, result.Length);
    }

    [Fact]
    public void Resample_EmptyData_ReturnsEmpty()
    {
        var result = AudioConverter.Resample(ReadOnlySpan<byte>.Empty, 16000, 48000);

        Assert.Empty(result);
    }

    [Fact]
    public void Resample_Silence_RemainsZero()
    {
        var silence = new byte[100];

        var result = AudioConverter.Resample(silence, 16000, 48000);

        Assert.All(result, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Resample_16kTo48k_TriplesSampleCount()
    {
        // 10 samples at 16kHz -> 30 samples at 48kHz
        var data = new byte[20]; // 10 samples

        var result = AudioConverter.Resample(data, 16000, 48000);

        Assert.Equal(60, result.Length); // 30 samples * 2 bytes
    }

    [Fact]
    public void Resample_48kTo16k_ThirdsSampleCount()
    {
        // 30 samples at 48kHz -> 10 samples at 16kHz
        var data = new byte[60]; // 30 samples

        var result = AudioConverter.Resample(data, 48000, 16000);

        Assert.Equal(20, result.Length); // 10 samples * 2 bytes
    }

    #endregion

    #region BuildWavBytes

    [Fact]
    public void BuildWavBytes_HasCorrectHeader()
    {
        var samples = new float[] { 0f, 0f };

        var result = AudioConverter.BuildWavBytes(samples, 16000);

        // Check RIFF header
        Assert.Equal((byte)'R', result[0]);
        Assert.Equal((byte)'I', result[1]);
        Assert.Equal((byte)'F', result[2]);
        Assert.Equal((byte)'F', result[3]);

        // Check WAVE marker
        Assert.Equal((byte)'W', result[8]);
        Assert.Equal((byte)'A', result[9]);
        Assert.Equal((byte)'V', result[10]);
        Assert.Equal((byte)'E', result[11]);
    }

    [Fact]
    public void BuildWavBytes_HasCorrectLength()
    {
        var samples = new float[100];

        var result = AudioConverter.BuildWavBytes(samples, 16000);

        // 44 byte header + 100 samples * 2 bytes = 244
        Assert.Equal(244, result.Length);
    }

    [Fact]
    public void BuildWavBytes_HasCorrectSampleRate()
    {
        var samples = new float[] { 0f };

        var result = AudioConverter.BuildWavBytes(samples, 24000);

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(24));
        Assert.Equal(24000, sampleRate);
    }

    [Fact]
    public void BuildWavBytes_HasMonoChannel()
    {
        var samples = new float[] { 0f };

        var result = AudioConverter.BuildWavBytes(samples, 16000);

        var channels = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(22));
        Assert.Equal(1, channels);
    }

    [Fact]
    public void BuildWavBytes_Has16BitSamples()
    {
        var samples = new float[] { 0f };

        var result = AudioConverter.BuildWavBytes(samples, 16000);

        var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(result.AsSpan(34));
        Assert.Equal(16, bitsPerSample);
    }

    [Fact]
    public void BuildWavBytes_DataChunkMarker()
    {
        var samples = new float[] { 0f };

        var result = AudioConverter.BuildWavBytes(samples, 16000);

        Assert.Equal((byte)'d', result[36]);
        Assert.Equal((byte)'a', result[37]);
        Assert.Equal((byte)'t', result[38]);
        Assert.Equal((byte)'a', result[39]);
    }

    [Fact]
    public void BuildWavBytes_DataChunkSize()
    {
        var samples = new float[50];

        var result = AudioConverter.BuildWavBytes(samples, 16000);

        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(40));
        Assert.Equal(100, dataSize); // 50 samples * 2 bytes
    }

    #endregion

    #region OGG/Opus encode/decode round-trip

    [Fact]
    public void EncodeOggOpus_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => AudioConverter.EncodeOggOpus(null!));
    }

    [Fact]
    public void DecodeOggOpus_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => AudioConverter.DecodeOggOpus(null!));
    }

    [Fact]
    public void EncodeDecodeOggOpus_RoundTrip_PreservesApproximateLength()
    {
        // Create a simple 48kHz mono tone: 0.5 seconds = 24000 samples = 48000 bytes
        var sampleCount = 24000;
        var pcmData = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            // Simple sine-ish pattern (alternating positive/negative values)
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / 48000.0) * 16000);
            BinaryPrimitives.WriteInt16LittleEndian(pcmData.AsSpan(i * 2), value);
        }

        var encoded = AudioConverter.EncodeOggOpus(pcmData, 48000);
        Assert.NotEmpty(encoded);

        var decoded = AudioConverter.DecodeOggOpus(encoded);
        Assert.NotEmpty(decoded);

        // Decoded should be approximately the same length (Opus pads to frame boundaries)
        // Allow some tolerance for padding
        var decodedSamples = decoded.Length / 2;
        Assert.InRange(decodedSamples, sampleCount - 960, sampleCount + 960);
    }

    [Fact]
    public void EncodeOggOpus_ProducesValidOggHeader()
    {
        // Create minimal PCM data (960 samples = 1 Opus frame at 48kHz)
        var pcmData = new byte[960 * 2];

        var encoded = AudioConverter.EncodeOggOpus(pcmData, 48000);

        // OGG files start with "OggS"
        Assert.True(encoded.Length >= 4);
        Assert.Equal((byte)'O', encoded[0]);
        Assert.Equal((byte)'g', encoded[1]);
        Assert.Equal((byte)'g', encoded[2]);
        Assert.Equal((byte)'S', encoded[3]);
    }

    #endregion

    #region SpeechOptions

    [Fact]
    public void SpeechOptions_DefaultValues()
    {
        var options = new SpeechOptions();

        Assert.NotNull(options.Stt);
        Assert.NotNull(options.Tts);
    }

    [Fact]
    public void SttOptions_DefaultEngine_IsWhisper()
    {
        var options = new SttOptions();

        Assert.Equal("whisper", options.Engine);
    }

    [Fact]
    public void TtsOptions_DefaultEngine_IsKokoro()
    {
        var options = new TtsOptions();

        Assert.Equal("kokoro", options.Engine);
    }

    [Fact]
    public void TtsOptions_DefaultVoice_IsAfHeart()
    {
        var options = new TtsOptions();

        Assert.Equal("af_heart", options.KokoroVoice);
    }

    #endregion

    #region ApplyGain

    private static byte[] MakePcm(params short[] samples)
    {
        var result = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i * 2), samples[i]);
        }
        return result;
    }

    private static short[] ReadSamples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        }
        return samples;
    }

    [Fact]
    public void ApplyGain_UnityGain_ReturnsIdenticalSamples()
    {
        var pcm = MakePcm(100, -100, 5000, -5000, 32000, -32000);

        AudioConverter.ApplyGain(pcm, 1.0f);

        Assert.Equal(new short[] { 100, -100, 5000, -5000, 32000, -32000 }, ReadSamples(pcm));
    }

    [Fact]
    public void ApplyGain_DoubleGain_DoublesSamples()
    {
        var pcm = MakePcm(100, -100, 5000, -5000);

        AudioConverter.ApplyGain(pcm, 2.0f);

        Assert.Equal(new short[] { 200, -200, 10000, -10000 }, ReadSamples(pcm));
    }

    [Fact]
    public void ApplyGain_PositiveClipping_SaturatesToMaxInt16()
    {
        var pcm = MakePcm(20000, 25000, 32000);

        AudioConverter.ApplyGain(pcm, 2.0f);

        Assert.Equal(new short[] { 32767, 32767, 32767 }, ReadSamples(pcm));
    }

    [Fact]
    public void ApplyGain_NegativeClipping_SaturatesToMinInt16()
    {
        var pcm = MakePcm(-20000, -25000, -32000);

        AudioConverter.ApplyGain(pcm, 2.0f);

        Assert.Equal(new short[] { -32768, -32768, -32768 }, ReadSamples(pcm));
    }

    [Fact]
    public void ApplyGain_ZeroGain_SilencesSamples()
    {
        var pcm = MakePcm(100, -100, 5000, -5000);

        AudioConverter.ApplyGain(pcm, 0.0f);

        Assert.All(ReadSamples(pcm), sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void ApplyGain_FractionalGain_AttenuatesSamples()
    {
        var pcm = MakePcm(1000, -1000);

        AudioConverter.ApplyGain(pcm, 0.5f);

        Assert.Equal(new short[] { 500, -500 }, ReadSamples(pcm));
    }

    [Fact]
    public void ApplyGain_EmptyBuffer_NoThrow()
    {
        var pcm = Array.Empty<byte>();

        AudioConverter.ApplyGain(pcm, 2.0f);

        Assert.Empty(pcm);
    }

    #endregion
}
