using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechTogglesTests
{
    [Theory]
    [InlineData(true, true, true)]    // master on, sub on  -> effective on
    [InlineData(true, false, false)]  // master on, sub off -> effective off
    [InlineData(false, true, false)]  // master off         -> effective off
    [InlineData(false, false, false)]
    public void EffectiveStt_IsMasterAndSub(bool master, bool sub, bool expected)
    {
        var speech = new SpeechConfig { Enabled = master, Stt = new SttConfig { Enabled = sub } };
        Assert.Equal(expected, SpeechToggles.EffectiveStt(speech));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void EffectiveTts_IsMasterAndSub(bool master, bool sub, bool expected)
    {
        var speech = new SpeechConfig { Enabled = master, Tts = new TtsConfig { Enabled = sub } };
        Assert.Equal(expected, SpeechToggles.EffectiveTts(speech));
    }

    [Fact]
    public void Defaults_AreEnabled()
    {
        var speech = new SpeechConfig();
        Assert.True(SpeechToggles.EffectiveStt(speech));
        Assert.True(SpeechToggles.EffectiveTts(speech));
    }
}
