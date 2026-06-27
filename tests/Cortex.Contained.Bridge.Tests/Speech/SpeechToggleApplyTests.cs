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

    [Fact]
    public void RawState_MasterOff_ReportsStoredSubFlagsNotEffective()
    {
        var speech = new SpeechConfig();
        SpeechToggleApply.Apply(speech, speechEnabled: false, sttEnabled: null, ttsEnabled: null, voiceIdEnabled: null);

        var state = SpeechToggleApply.RawState(speech);

        Assert.False(state.SpeechEnabled);
        // Sub-flags must report STORED intent (still true), NOT the master-gated effective
        // value — otherwise the UI writes the wiped values back and re-enabling the master
        // would persist STT/TTS/voice-id as disabled.
        Assert.True(state.SttEnabled);
        Assert.True(state.TtsEnabled);
        Assert.True(state.VoiceIdEnabled);
    }

    [Theory]
    [InlineData(false, true, true, true)]    // enable transition, compose ok -> restart needed (verifier attaches at construction)
    [InlineData(true, false, true, false)]   // disable transition, compose ok -> live (sidecar stop), no restart
    [InlineData(true, true, true, false)]    // no change, compose ok -> no restart
    [InlineData(false, false, false, true)]  // compose failed -> restart needed regardless
    public void VoiceIdRestartRequired_Cases(bool before, bool after, bool composeConfirmed, bool expected)
    {
        Assert.Equal(expected, SpeechToggleApply.VoiceIdRestartRequired(before, after, composeConfirmed));
    }
}
