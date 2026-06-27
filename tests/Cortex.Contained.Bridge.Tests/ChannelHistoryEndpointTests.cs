using Cortex.Contained.Bridge.Tenants;

namespace Cortex.Contained.Bridge.Tests;

/// <summary>
/// Unit tests for the per-channel history endpoint helpers
/// (<see cref="ChannelHistoryEndpoints"/>). These cover the pure input-handling logic:
/// channel id URL decoding, emptiness checks, and ISO-8601 <c>olderThan</c> parsing.
/// </summary>
public class ChannelHistoryEndpointTests
{
    [Theory]
    [InlineData("webchat-default", "webchat-default")]
    [InlineData("discord-voice-default", "discord-voice-default")]
    [InlineData("discord%2Fdm", "discord/dm")]
    [InlineData("api-tenant-a", "api-tenant-a")]
    public void TryParseChannelId_ValidInputs_ReturnsDecoded(string raw, string expected)
    {
        var parsed = ChannelHistoryEndpoints.TryParseChannelId(raw, out var decoded);

        Assert.True(parsed);
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%20%20")] // whitespace-only after decoding
    public void TryParseChannelId_EmptyOrWhitespace_ReturnsFalse(string? raw)
    {
        var parsed = ChannelHistoryEndpoints.TryParseChannelId(raw, out var decoded);

        Assert.False(parsed);
        Assert.Null(decoded);
    }

    [Fact]
    public void TryParseOlderThan_Missing_ReturnsTrueAndMaxValue()
    {
        var ok = ChannelHistoryEndpoints.TryParseOlderThan(null, out var cutoff);

        Assert.True(ok);
        Assert.Equal(DateTimeOffset.MaxValue, cutoff);
    }

    [Fact]
    public void TryParseOlderThan_Empty_ReturnsTrueAndMaxValue()
    {
        var ok = ChannelHistoryEndpoints.TryParseOlderThan("", out var cutoff);

        Assert.True(ok);
        Assert.Equal(DateTimeOffset.MaxValue, cutoff);
    }

    [Fact]
    public void TryParseOlderThan_ValidIso8601_ReturnsTrueAndParsed()
    {
        var ok = ChannelHistoryEndpoints.TryParseOlderThan("2026-04-19T09:32:24.932Z", out var cutoff);

        Assert.True(ok);
        Assert.Equal(new DateTimeOffset(2026, 4, 19, 9, 32, 24, 932, TimeSpan.Zero), cutoff);
    }

    [Fact]
    public void TryParseOlderThan_InvalidFormat_ReturnsFalse()
    {
        var ok = ChannelHistoryEndpoints.TryParseOlderThan("not-a-date", out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParseOlderThan_InvalidNumericGarbage_ReturnsFalse()
    {
        var ok = ChannelHistoryEndpoints.TryParseOlderThan("1234567890", out _);

        Assert.False(ok);
    }
}
