using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechToggleApplyVoiceIdTests
{
    [Fact]
    public void Apply_SetsVoiceIdOnly()
    {
        var speech = new SpeechConfig();
        SpeechToggleApply.Apply(speech, speechEnabled: null, sttEnabled: null, ttsEnabled: null, voiceIdEnabled: false);
        Assert.True(speech.Enabled);
        Assert.True(speech.Stt.Enabled);
        Assert.True(speech.Tts.Enabled);
        Assert.False(speech.VoiceId.Enabled);
    }

    [Fact]
    public void Apply_NullVoiceId_LeavesUnchanged()
    {
        var speech = new SpeechConfig { VoiceId = new VoiceIdConfig { Enabled = false } };
        SpeechToggleApply.Apply(speech, null, null, null, voiceIdEnabled: null);
        Assert.False(speech.VoiceId.Enabled);
    }
}
