using Cortex.Contained.Agent.Host.Tools;

namespace Cortex.Contained.Agent.Host.Tests;

public class ChannelNameResolverTests
{
    // ── Resolve ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("discord", "discord-dm")]
    [InlineData("discord-dm", "discord-dm")]
    [InlineData("discord-guild", "discord-guild")]
    [InlineData("discord-voice", "discord-voice")]
    [InlineData("webchat", "webchat-default")]
    [InlineData("webchat-default", "webchat-default")]
    [InlineData("voice", "voice-default")]
    [InlineData("voice-default", "voice-default")]
    public void Resolve_ValidNames_ReturnsCanonicalId(string input, string expected)
    {
        Assert.Equal(expected, ChannelNameResolver.Resolve(input));
    }

    [Theory]
    [InlineData("DISCORD")]
    [InlineData("Discord")]
    [InlineData("WEBCHAT")]
    [InlineData("Voice")]
    public void Resolve_CaseInsensitive(string input)
    {
        Assert.NotNull(ChannelNameResolver.Resolve(input));
    }

    [Theory]
    [InlineData("telegram")]
    [InlineData("slack")]
    [InlineData("")]
    [InlineData("discord-unknown")]
    [InlineData("web")]
    public void Resolve_InvalidNames_ReturnsNull(string input)
    {
        Assert.Null(ChannelNameResolver.Resolve(input));
    }

    // ── TryResolve ───────────────────────────────────────────────────────

    [Fact]
    public void TryResolve_ValidName_ReturnsTrueAndSetsChannelId()
    {
        var result = ChannelNameResolver.TryResolve("discord", out var channelId);

        Assert.True(result);
        Assert.Equal("discord-dm", channelId);
    }

    [Fact]
    public void TryResolve_InvalidName_ReturnsFalseAndNullChannelId()
    {
        var result = ChannelNameResolver.TryResolve("telegram", out var channelId);

        Assert.False(result);
        Assert.Null(channelId);
    }

    // ── ValidChannelNames ────────────────────────────────────────────────

    [Fact]
    public void ValidChannelNames_ContainsAllExpectedNames()
    {
        var names = ChannelNameResolver.ValidChannelNames;

        Assert.Contains("webchat", names);
        Assert.Contains("discord", names);
        Assert.Contains("discord-dm", names);
        Assert.Contains("discord-guild", names);
        Assert.Contains("discord-voice", names);
        Assert.Contains("voice", names);
    }

    [Fact]
    public void ValidChannelNames_DistinguishesDiscordVoiceFromLocalVoice()
    {
        // Regression guard: "voice" is the local PC speaker, "discord-voice" is
        // the Discord voice channel. The LLM must see both options separately —
        // otherwise it will say "voice" when the user asks for Discord voice
        // and the message will be delivered to PC speakers instead.
        var names = ChannelNameResolver.ValidChannelNames;

        Assert.Contains("discord-voice", names);
        Assert.Contains("voice", names);
        // They must appear as distinct tokens.
        Assert.NotEqual(names.IndexOf("discord-voice", StringComparison.Ordinal),
                        names.IndexOf("voice", StringComparison.Ordinal));
    }

    [Fact]
    public void ToFriendlyName_DiscordVoice_ReturnsDiscordVoice()
    {
        Assert.Equal("discord-voice", ChannelNameResolver.ToFriendlyName("discord-voice"));
    }

    // ── IsChannelActive ──────────────────────────────────────────────────

    [Fact]
    public void IsChannelActive_EmptyActiveList_ReturnsTrue()
    {
        // When no active channels have been received yet, all channels are allowed
        Assert.True(ChannelNameResolver.IsChannelActive("webchat-default", []));
        Assert.True(ChannelNameResolver.IsChannelActive("discord-dm", []));
        Assert.True(ChannelNameResolver.IsChannelActive("anything", []));
    }

    [Fact]
    public void IsChannelActive_ChannelInActiveList_ReturnsTrue()
    {
        var active = new[] { "webchat-default", "discord-dm" };

        Assert.True(ChannelNameResolver.IsChannelActive("webchat-default", active));
        Assert.True(ChannelNameResolver.IsChannelActive("discord-dm", active));
    }

    [Fact]
    public void IsChannelActive_ChannelNotInActiveList_ReturnsFalse()
    {
        var active = new[] { "webchat-default" };

        Assert.False(ChannelNameResolver.IsChannelActive("discord-dm", active));
        Assert.False(ChannelNameResolver.IsChannelActive("voice-default", active));
    }

    // ── GetValidChannelNames with active filter ──────────────────────────

    [Fact]
    public void GetValidChannelNames_NullActiveChannels_ReturnsAllNames()
    {
        var names = ChannelNameResolver.GetValidChannelNames(null);

        Assert.Equal(ChannelNameResolver.ValidChannelNames, names);
    }

    [Fact]
    public void GetValidChannelNames_EmptyActiveChannels_ReturnsAllNames()
    {
        var names = ChannelNameResolver.GetValidChannelNames([]);

        Assert.Equal(ChannelNameResolver.ValidChannelNames, names);
    }

    [Fact]
    public void GetValidChannelNames_WithActiveChannels_FiltersToActiveOnly()
    {
        var active = new[] { "webchat-default", "discord-dm" };

        var names = ChannelNameResolver.GetValidChannelNames(active);

        Assert.Contains("discord", names);
        Assert.Contains("discord-dm", names);
        Assert.Contains("webchat", names);
        Assert.Contains("webchat-default", names);
        Assert.DoesNotContain("discord-guild", names);
        Assert.DoesNotContain("voice", names);
    }

    [Fact]
    public void GetValidChannelNames_WithSingleActiveChannel_ReturnsOnlyMatchingAliases()
    {
        var active = new[] { "voice-default" };

        var names = ChannelNameResolver.GetValidChannelNames(active);

        Assert.Contains("voice", names);
        Assert.Contains("voice-default", names);
        Assert.DoesNotContain("webchat", names);
        Assert.DoesNotContain("discord", names);
    }

    [Fact]
    public void GetValidChannelNames_DiscordVoiceActive_IncludesDiscordVoice()
    {
        var active = new[] { "discord-voice" };

        var names = ChannelNameResolver.GetValidChannelNames(active);

        Assert.Contains("discord-voice", names);
        // Local "voice" is a different channel and should not be offered.
        Assert.DoesNotContain("voice-default", names);
    }
}
