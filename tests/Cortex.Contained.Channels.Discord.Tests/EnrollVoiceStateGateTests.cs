namespace Cortex.Contained.Channels.Discord.Tests;

using Cortex.Contained.Channels.Discord;

public sealed class EnrollVoiceStateGateTests
{
    [Fact]
    public void Decide_UserInConfiguredChannel_Proceeds()
    {
        var decision = EnrollVoiceStateGate.Decide(currentVoiceChannelId: 42UL, configuredVoiceChannelId: 42UL);
        Assert.Equal(EnrollGateDecision.Proceed, decision);
    }

    [Fact]
    public void Decide_UserInDifferentChannel_RingsAndProceeds()
    {
        var decision = EnrollVoiceStateGate.Decide(currentVoiceChannelId: 99UL, configuredVoiceChannelId: 42UL);
        Assert.Equal(EnrollGateDecision.RingAndProceed, decision);
    }

    [Fact]
    public void Decide_UserNotInAnyChannel_RingsAndProceeds()
    {
        var decision = EnrollVoiceStateGate.Decide(currentVoiceChannelId: null, configuredVoiceChannelId: 42UL);
        Assert.Equal(EnrollGateDecision.RingAndProceed, decision);
    }
}
