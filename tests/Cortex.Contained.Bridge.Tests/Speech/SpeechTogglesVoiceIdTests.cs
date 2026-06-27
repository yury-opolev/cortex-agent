using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechTogglesVoiceIdTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void EffectiveVoiceId_IsMasterAndSub(bool master, bool sub, bool expected)
    {
        var speech = new SpeechConfig { Enabled = master, VoiceId = new VoiceIdConfig { Enabled = sub } };
        Assert.Equal(expected, SpeechToggles.EffectiveVoiceId(speech));
    }

    [Fact]
    public void EffectiveVoiceId_DefaultsEnabled()
    {
        Assert.True(SpeechToggles.EffectiveVoiceId(new SpeechConfig()));
    }
}
