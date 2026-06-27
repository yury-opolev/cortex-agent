using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class DiscordInviteParserTests
{
    // ── TryExtractInviteCode ────────────────────────────────────────────

    [Theory]
    [InlineData("https://discord.gg/TTQEUKmyn", "TTQEUKmyn")]
    [InlineData("http://discord.gg/abc123", "abc123")]
    [InlineData("discord.gg/abc123", "abc123")]
    [InlineData("https://discord.com/invite/abc123", "abc123")]
    [InlineData("https://discordapp.com/invite/abc123", "abc123")]
    [InlineData("https://www.discord.gg/abc123", "abc123")]
    [InlineData("Hey come look: https://discord.gg/xyz9 now", "xyz9")]
    [InlineData("HTTPS://DISCORD.GG/CASE", "CASE")]
    [InlineData("here's two: https://discord.gg/first and https://discord.gg/second", "first")]
    public void TryExtractInviteCode_ValidLink_ReturnsCode(string content, string expected)
    {
        var ok = DiscordInviteParser.TryExtractInviteCode(content, out var code);

        Assert.True(ok);
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello there")]
    [InlineData("https://example.com/invite/abc")]
    [InlineData("https://discord.gg/")]
    [InlineData("discord.gg")]
    public void TryExtractInviteCode_NotAnInvite_ReturnsFalse(string? content)
    {
        var ok = DiscordInviteParser.TryExtractInviteCode(content, out var code);

        Assert.False(ok);
        Assert.Equal(string.Empty, code);
    }

    // ── IsInviteOnly ────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://discord.gg/TTQEUKmyn")]
    [InlineData("  https://discord.gg/TTQEUKmyn  ")]
    [InlineData("discord.gg/abc")]
    [InlineData("https://discord.com/invite/abc123")]
    public void IsInviteOnly_PureInviteLink_ReturnsTrue(string content)
    {
        Assert.True(DiscordInviteParser.IsInviteOnly(content));
    }

    [Theory]
    [InlineData("hey https://discord.gg/abc")]
    [InlineData("https://discord.gg/abc hey")]
    [InlineData("look at this: https://discord.gg/abc — cool")]
    [InlineData("hello there")]
    [InlineData("")]
    [InlineData(null)]
    public void IsInviteOnly_MixedOrUnrelated_ReturnsFalse(string? content)
    {
        Assert.False(DiscordInviteParser.IsInviteOnly(content));
    }
}
