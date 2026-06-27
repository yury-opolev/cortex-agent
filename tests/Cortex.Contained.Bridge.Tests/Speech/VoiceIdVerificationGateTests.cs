using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Bridge.Tests.Speech;

public sealed class VoiceIdVerificationGateTests
{
    [Fact]
    public void Select_Enabled_ReturnsVerifier()
    {
        var verifier = Substitute.For<ISpeakerVerifier>();
        var speech = new SpeechConfig(); // voice-id on by default
        Assert.Same(verifier, VoiceIdVerifierSelector.Select(verifier, speech));
    }

    [Fact]
    public void Select_Disabled_ReturnsNull()
    {
        var verifier = Substitute.For<ISpeakerVerifier>();
        var speech = new SpeechConfig { VoiceId = new VoiceIdConfig { Enabled = false } };
        Assert.Null(VoiceIdVerifierSelector.Select(verifier, speech));
    }

    [Fact]
    public void Select_MasterOff_ReturnsNull()
    {
        var verifier = Substitute.For<ISpeakerVerifier>();
        var speech = new SpeechConfig { Enabled = false };
        Assert.Null(VoiceIdVerifierSelector.Select(verifier, speech));
    }
}
