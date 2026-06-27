using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Tests the pure voice-state routing policy that decides what the voice handler
/// does when the linked user joins/leaves the target channel. Pure static helper
/// so it's testable without Discord / audio.
///
/// Regression focus: a voice connection is "alive" only when the audio transport
/// is actually connected. A stale <see cref="Discord.Audio.IAudioClient"/> left
/// behind by a gateway reconnect (reference non-null but transport dead) MUST
/// route a user-join to a rejoin — not be treated as "already connected, do
/// nothing". That stale-reference bug stranded Discord voice on 2026-05-15.
/// </summary>
public class VoiceStateRouterTests
{
    [Fact]
    public void Route_UserJoined_ConnectionDead_Rejoins(/* the 2026-05-15 bug */)
    {
        var action = VoiceStateRouter.Route(
            joinedTarget: true,
            leftTarget: false,
            connectionAlive: false,
            otherNonBotUsersPresent: false);

        Assert.Equal(VoiceStateAction.JoinAndDrainProactive, action);
    }

    [Fact]
    public void Route_UserJoined_ConnectionAlive_DrainsProactiveOnly()
    {
        var action = VoiceStateRouter.Route(
            joinedTarget: true,
            leftTarget: false,
            connectionAlive: true,
            otherNonBotUsersPresent: false);

        Assert.Equal(VoiceStateAction.DrainProactive, action);
    }

    [Fact]
    public void Route_UserLeft_NoOtherUsers_Leaves()
    {
        var action = VoiceStateRouter.Route(
            joinedTarget: false,
            leftTarget: true,
            connectionAlive: true,
            otherNonBotUsersPresent: false);

        Assert.Equal(VoiceStateAction.Leave, action);
    }

    [Fact]
    public void Route_UserLeft_OtherUsersStillPresent_StaysConnected()
    {
        var action = VoiceStateRouter.Route(
            joinedTarget: false,
            leftTarget: true,
            connectionAlive: true,
            otherNonBotUsersPresent: true);

        Assert.Equal(VoiceStateAction.None, action);
    }

    [Fact]
    public void Route_UserLeft_NotConnected_NothingToDo()
    {
        var action = VoiceStateRouter.Route(
            joinedTarget: false,
            leftTarget: true,
            connectionAlive: false,
            otherNonBotUsersPresent: false);

        Assert.Equal(VoiceStateAction.None, action);
    }

    [Fact]
    public void Route_UnrelatedVoiceStateUpdate_NothingToDo()
    {
        var action = VoiceStateRouter.Route(
            joinedTarget: false,
            leftTarget: false,
            connectionAlive: true,
            otherNonBotUsersPresent: true);

        Assert.Equal(VoiceStateAction.None, action);
    }
}
