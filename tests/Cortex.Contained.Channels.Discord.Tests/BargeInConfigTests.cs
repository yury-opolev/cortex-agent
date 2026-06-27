using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class BargeInConfigTests
{
    [Fact]
    public void Defaults_GuardAndClassifierMode()
    {
        var o = new DiscordChannelOptions { BotToken = "x" };
        Assert.Equal(150, o.BargeInOnsetGuardMs);
        Assert.Equal(BargeInClassifierMode.HeuristicPlusLlm, o.BargeInClassifierMode);
        Assert.True(o.EnableBargeIn);
    }
}
