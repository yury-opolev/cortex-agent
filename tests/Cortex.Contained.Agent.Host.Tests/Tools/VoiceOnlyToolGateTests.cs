using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public class VoiceOnlyToolGateTests
{
    [Fact]
    public void GetHiddenTools_NonVoiceConversation_HidesVoiceOnlyTools()
    {
        var gate = new VoiceOnlyToolGate();

        var hidden = gate.GetHiddenTools("webchat-default");

        Assert.Equal(6, hidden.Count);
        Assert.Contains("start_voice_enrollment", hidden);

        // session_timer is deliberately NOT voice-gated any more: a fired timer delivers through
        // whatever the model chooses, so timers are useful in text conversations too.
        Assert.DoesNotContain("session_timer", hidden);
    }

    [Fact]
    public void GetHiddenTools_VoiceConversation_HidesNothing()
    {
        var gate = new VoiceOnlyToolGate();

        var hidden = gate.GetHiddenTools("discord-voice-123");

        Assert.Empty(hidden);
    }

    [Fact]
    public void GetHiddenTools_NullConversation_HidesVoiceOnlyTools()
    {
        var gate = new VoiceOnlyToolGate();

        var hidden = gate.GetHiddenTools(null);

        Assert.NotEmpty(hidden);
    }
}
