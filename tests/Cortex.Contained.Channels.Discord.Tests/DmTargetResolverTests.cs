using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class DmTargetResolverTests
{
    [Fact]
    public void Resolve_HasSnowflake_UsesIt()
    {
        var (action, value) = DmTargetResolver.Resolve(dmChannelSnowflake: 123, dmRecipientUserId: 999);

        Assert.Equal(DmTargetResolver.DmAction.UseSnowflake, action);
        Assert.Equal(123ul, value);
    }

    [Fact]
    public void Resolve_NoSnowflake_HasRecipient_OpensDm()
    {
        var (action, value) = DmTargetResolver.Resolve(dmChannelSnowflake: 0, dmRecipientUserId: 999);

        Assert.Equal(DmTargetResolver.DmAction.OpenDmForUser, action);
        Assert.Equal(999ul, value);
    }

    [Fact]
    public void Resolve_NoSnowflake_NoRecipient_NoTarget()
    {
        var (action, _) = DmTargetResolver.Resolve(dmChannelSnowflake: 0, dmRecipientUserId: 0);

        Assert.Equal(DmTargetResolver.DmAction.NoTarget, action);
    }
}
