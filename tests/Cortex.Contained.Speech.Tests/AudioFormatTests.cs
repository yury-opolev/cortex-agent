namespace Cortex.Contained.Speech.Tests;

public class AudioFormatTests
{
    #region Static Instances

    [Fact]
    public void Whisper_HasExpectedValues()
    {
        var format = AudioFormat.Whisper;

        Assert.Equal(16_000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Fact]
    public void Kokoro_HasExpectedValues()
    {
        var format = AudioFormat.Kokoro;

        Assert.Equal(24_000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    [Fact]
    public void Discord_HasExpectedValues()
    {
        var format = AudioFormat.Discord;

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
    }

    #endregion

    #region Computed Properties

    [Fact]
    public void BytesPerSample_16Bit_Returns2()
    {
        Assert.Equal(2, AudioFormat.Whisper.BytesPerSample);
    }

    [Fact]
    public void BlockAlign_Mono16Bit_Returns2()
    {
        Assert.Equal(2, AudioFormat.Whisper.BlockAlign);
    }

    [Fact]
    public void BlockAlign_Stereo16Bit_Returns4()
    {
        var stereo = new AudioFormat(44_100, 2, 16);

        Assert.Equal(4, stereo.BlockAlign);
    }

    [Fact]
    public void BytesPerSecond_Whisper_Returns32000()
    {
        Assert.Equal(32_000, AudioFormat.Whisper.BytesPerSecond);
    }

    [Fact]
    public void BytesPerSecond_Kokoro_Returns48000()
    {
        Assert.Equal(48_000, AudioFormat.Kokoro.BytesPerSecond);
    }

    [Fact]
    public void BytesPerSecond_Discord_Returns96000()
    {
        Assert.Equal(96_000, AudioFormat.Discord.BytesPerSecond);
    }

    #endregion

    #region MillisecondsToBytes

    [Fact]
    public void MillisecondsToBytes_Whisper_OneSecond_Returns32000()
    {
        Assert.Equal(32_000, AudioFormat.Whisper.MillisecondsToBytes(1000));
    }

    [Fact]
    public void MillisecondsToBytes_Kokoro_OneSecond_Returns48000()
    {
        Assert.Equal(48_000, AudioFormat.Kokoro.MillisecondsToBytes(1000));
    }

    [Fact]
    public void MillisecondsToBytes_Discord_OneSecond_Returns96000()
    {
        Assert.Equal(96_000, AudioFormat.Discord.MillisecondsToBytes(1000));
    }

    [Fact]
    public void MillisecondsToBytes_Zero_ReturnsZero()
    {
        Assert.Equal(0, AudioFormat.Whisper.MillisecondsToBytes(0));
    }

    [Fact]
    public void MillisecondsToBytes_500Ms_Whisper_Returns16000()
    {
        Assert.Equal(16_000, AudioFormat.Whisper.MillisecondsToBytes(500));
    }

    #endregion

    #region Record Equality

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new AudioFormat(16_000, 1, 16);
        var b = new AudioFormat(16_000, 1, 16);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Equality_DifferentSampleRate_AreNotEqual()
    {
        var a = new AudioFormat(16_000, 1, 16);
        var b = new AudioFormat(24_000, 1, 16);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_StaticInstanceMatchesManual()
    {
        Assert.Equal(new AudioFormat(16_000, 1, 16), AudioFormat.Whisper);
    }

    #endregion
}
