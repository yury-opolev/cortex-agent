namespace Cortex.Contained.Channels.Voice.Tests;

public class AudioHelpersTests
{
    #region Constants

    [Fact]
    public void SampleRate_Is16000()
    {
        Assert.Equal(16_000, AudioHelpers.SampleRate);
    }

    [Fact]
    public void Channels_Is1()
    {
        Assert.Equal(1, AudioHelpers.Channels);
    }

    [Fact]
    public void BytesPerSample_Is2()
    {
        Assert.Equal(2, AudioHelpers.BytesPerSample);
    }

    [Fact]
    public void BytesPerSecond_Is32000()
    {
        Assert.Equal(32_000, AudioHelpers.BytesPerSecond);
    }

    #endregion

    #region ComputeRms

    [Fact]
    public void ComputeRms_EmptyArray_ReturnsZero()
    {
        var result = AudioHelpers.ComputeRms(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ComputeRms_SingleByte_ReturnsZero()
    {
        // Less than BytesPerSample (2)
        var result = AudioHelpers.ComputeRms(new byte[] { 0xFF });

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ComputeRms_AllZeros_ReturnsZero()
    {
        // Silence = all zero samples
        var silence = new byte[100];

        var result = AudioHelpers.ComputeRms(silence);

        Assert.Equal(0f, result);
    }

    [Fact]
    public void ComputeRms_MaxAmplitude_ReturnsOne()
    {
        // A single sample at max positive value (short.MaxValue = 32767 = 0x7FFF)
        // Little-endian: low byte first
        var data = new byte[] { 0xFF, 0x7F };

        var result = AudioHelpers.ComputeRms(data);

        Assert.Equal(1.0f, result, 0.001f);
    }

    [Fact]
    public void ComputeRms_MixedSamples_ReturnsExpectedValue()
    {
        // Two samples: +16383 (half amplitude) and -16383
        // +16383 = 0x3FFF little-endian: 0xFF, 0x3F
        // -16383 = 0xC001 as signed = 0x01, 0xC0 little-endian
        var data = new byte[] { 0xFF, 0x3F, 0x01, 0xC0 };

        var result = AudioHelpers.ComputeRms(data);

        // Both samples have similar magnitude, so RMS ≈ 16383/32767 ≈ 0.5
        Assert.True(result > 0.4f && result < 0.6f);
    }

    [Fact]
    public void ComputeRms_OddByteCount_IgnoresTrailingByte()
    {
        // 3 bytes = 1 complete sample + 1 trailing byte
        var data = new byte[] { 0xFF, 0x7F, 0x00 };

        var result = AudioHelpers.ComputeRms(data);

        // Should only process the first 2-byte sample
        Assert.Equal(1.0f, result, 0.001f);
    }

    #endregion

    #region PcmToFloat

    [Fact]
    public void PcmToFloat_EmptyArray_ReturnsEmptyArray()
    {
        var result = AudioHelpers.PcmToFloat(ReadOnlySpan<byte>.Empty);

        Assert.Empty(result);
    }

    [Fact]
    public void PcmToFloat_Silence_ReturnsAllZeros()
    {
        var silence = new byte[8]; // 4 samples of silence

        var result = AudioHelpers.PcmToFloat(silence);

        Assert.Equal(4, result.Length);
        Assert.All(result, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void PcmToFloat_MaxPositive_ReturnsOne()
    {
        // short.MaxValue = 32767 = 0x7FFF
        var data = new byte[] { 0xFF, 0x7F };

        var result = AudioHelpers.PcmToFloat(data);

        Assert.Single(result);
        Assert.Equal(1.0f, result[0], 0.001f);
    }

    [Fact]
    public void PcmToFloat_MaxNegative_ReturnsNegativeOne()
    {
        // short.MinValue = -32768 = 0x8000 little-endian: 0x00, 0x80
        var data = new byte[] { 0x00, 0x80 };

        var result = AudioHelpers.PcmToFloat(data);

        Assert.Single(result);
        // -32768 / 32767 ≈ -1.00003, but float precision makes this close to -1
        Assert.True(result[0] < -0.99f);
    }

    [Fact]
    public void PcmToFloat_MultipleSamples_ReturnsCorrectCount()
    {
        var data = new byte[20]; // 10 samples

        var result = AudioHelpers.PcmToFloat(data);

        Assert.Equal(10, result.Length);
    }

    #endregion

    #region MillisecondsToBytes

    [Fact]
    public void MillisecondsToBytes_OneSecond_Returns32000()
    {
        var result = AudioHelpers.MillisecondsToBytes(1000);

        Assert.Equal(32_000, result);
    }

    [Fact]
    public void MillisecondsToBytes_500Ms_Returns16000()
    {
        var result = AudioHelpers.MillisecondsToBytes(500);

        Assert.Equal(16_000, result);
    }

    [Fact]
    public void MillisecondsToBytes_Zero_ReturnsZero()
    {
        var result = AudioHelpers.MillisecondsToBytes(0);

        Assert.Equal(0, result);
    }

    [Fact]
    public void MillisecondsToBytes_100Ms_Returns3200()
    {
        var result = AudioHelpers.MillisecondsToBytes(100);

        Assert.Equal(3_200, result);
    }

    [Fact]
    public void MillisecondsToBytes_1500Ms_Returns48000()
    {
        // Default silence timeout
        var result = AudioHelpers.MillisecondsToBytes(1500);

        Assert.Equal(48_000, result);
    }

    [Fact]
    public void MillisecondsToBytes_3000Ms_Returns96000()
    {
        // Default wake word segment
        var result = AudioHelpers.MillisecondsToBytes(3000);

        Assert.Equal(96_000, result);
    }

    #endregion
}
