using Cortex.Contained.Agent.Host.SpeakerId;
using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public sealed class VoiceIdDisabledToolGateTests
{
    [Fact]
    public void Enabled_HidesNothing()
    {
        var store = new SpeakerIdSettingsStore(); // default enabled
        Assert.Empty(new VoiceIdDisabledToolGate(store).GetHiddenTools("voice:abc"));
    }

    [Fact]
    public void Disabled_HidesEnrollmentTools()
    {
        var store = new SpeakerIdSettingsStore();
        store.SetEnabled(false);
        var hidden = new VoiceIdDisabledToolGate(store).GetHiddenTools("voice:abc");

        Assert.Contains("start_voice_enrollment", hidden);
        Assert.Contains("forget_voice_enrollment", hidden);
        Assert.DoesNotContain("speak_after_delay", hidden); // TTS tool, not voice-id
    }
}
