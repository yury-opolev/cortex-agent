using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class SpeechToggleApplyTests
{
    [Fact]
    public void Apply_SetsOnlyProvidedFlags()
    {
        var speech = new SpeechConfig(); // all true by default
        SpeechToggleApply.Apply(speech, speechEnabled: false, sttEnabled: null, ttsEnabled: null, voiceIdEnabled: null);

        Assert.False(speech.Enabled);
        Assert.True(speech.Stt.Enabled);   // unchanged (null)
        Assert.True(speech.Tts.Enabled);   // unchanged (null)
    }

    [Fact]
    public void Apply_UpdatesSubFlags()
    {
        var speech = new SpeechConfig();
        SpeechToggleApply.Apply(speech, speechEnabled: null, sttEnabled: false, ttsEnabled: true, voiceIdEnabled: null);

        Assert.True(speech.Enabled);
        Assert.False(speech.Stt.Enabled);
        Assert.True(speech.Tts.Enabled);
    }
}
