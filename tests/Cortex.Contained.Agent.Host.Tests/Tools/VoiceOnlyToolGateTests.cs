using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public class VoiceOnlyToolGateTests
{
    [Fact]
    public void GetHiddenTools_NonVoiceConversation_HidesVoiceOnlyTools()
    {
        var gate = new VoiceOnlyToolGate();

        var hidden = gate.GetHiddenTools("webchat-default");

        Assert.Equal(8, hidden.Count);
        Assert.Contains("speak_after_delay", hidden);
        Assert.Contains("start_voice_enrollment", hidden);
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
