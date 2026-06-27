using Cortex.Contained.Channels.Discord;
using Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Pins the connection-liveness predicate behind <c>DiscordVoiceHandler.IsConnected</c>.
/// The 2026-05-15 outage was exactly this predicate being wrong: a stale audio
/// client (reference present but transport not <see cref="ConnectionState.Connected"/>)
/// was treated as "connected", so the bot never rejoined. Only
/// <see cref="ConnectionState.Connected"/> counts as alive.
/// </summary>
public class VoiceConnectionStateTests
{
    [Fact]
    public void IsAlive_Connected_True()
    {
        Assert.True(VoiceConnectionState.IsAlive(ConnectionState.Connected));
    }

    [Fact]
    public void IsAlive_Disconnected_False(/* the stale post-reconnect client */)
    {
        Assert.False(VoiceConnectionState.IsAlive(ConnectionState.Disconnected));
    }

    [Fact]
    public void IsAlive_Connecting_False()
    {
        Assert.False(VoiceConnectionState.IsAlive(ConnectionState.Connecting));
    }

    [Fact]
    public void IsAlive_Disconnecting_False()
    {
        Assert.False(VoiceConnectionState.IsAlive(ConnectionState.Disconnecting));
    }

    [Fact]
    public void IsAlive_Null_False(/* no audio client at all */)
    {
        Assert.False(VoiceConnectionState.IsAlive(null));
    }
}
