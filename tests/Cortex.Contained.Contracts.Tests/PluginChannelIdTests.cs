using Cortex.Contained.Contracts.Channels;

namespace Cortex.Contained.Contracts.Tests;

public class PluginChannelIdTests
{
    // ── Create ────────────────────────────────────────────────────────────

    [Fact]
    public void Create_ReturnsExpectedFormat()
    {
        var id = PluginChannelId.Create("terminal", "default");

        Assert.Equal("plugin:terminal:default", id);
    }

    // ── TryParse ──────────────────────────────────────────────────────────

    [Fact]
    public void TryParse_ValidId_ReturnsKeyAndInstance()
    {
        var ok = PluginChannelId.TryParse("plugin:terminal:default", out var key, out var instanceId);

        Assert.True(ok);
        Assert.Equal("terminal", key);
        Assert.Equal("default", instanceId);
    }

    [Fact]
    public void TryParse_WrongPrefix_ReturnsFalse()
    {
        var ok = PluginChannelId.TryParse("webchat:terminal:default", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_TooFewSegments_ReturnsFalse()
    {
        var ok = PluginChannelId.TryParse("plugin:terminal", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_TooManySegments_ReturnsFalse()
    {
        var ok = PluginChannelId.TryParse("plugin:a:b:c", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse()
    {
        var ok = PluginChannelId.TryParse("", out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParse_InvalidSegment_ReturnsFalse()
    {
        var ok = PluginChannelId.TryParse("plugin:Terminal:default", out _, out _);

        Assert.False(ok);
    }

    // ── IsPluginChannelId ─────────────────────────────────────────────────

    [Fact]
    public void IsPluginChannelId_ValidId_ReturnsTrue()
    {
        Assert.True(PluginChannelId.IsPluginChannelId("plugin:my-key:inst_1"));
    }

    [Fact]
    public void IsPluginChannelId_Invalid_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsPluginChannelId("discord:something"));
    }

    // ── IsValidSegment ────────────────────────────────────────────────────

    [Theory]
    [InlineData("terminal")]
    [InlineData("my-key")]
    [InlineData("inst_1")]
    [InlineData("a")]
    public void IsValidSegment_ValidInputs_ReturnsTrue(string segment)
    {
        Assert.True(PluginChannelId.IsValidSegment(segment));
    }

    [Fact]
    public void IsValidSegment_Null_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment(null));
    }

    [Fact]
    public void IsValidSegment_Empty_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment(""));
    }

    [Fact]
    public void IsValidSegment_TooLong_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment(new string('a', 65)));
    }

    [Fact]
    public void IsValidSegment_UpperCase_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("Terminal"));
    }

    [Fact]
    public void IsValidSegment_Colon_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("key:name"));
    }

    [Fact]
    public void IsValidSegment_Dot_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("key.name"));
    }

    [Fact]
    public void IsValidSegment_Slash_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("key/name"));
    }

    [Fact]
    public void IsValidSegment_Whitespace_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("key name"));
    }

    [Fact]
    public void IsValidSegment_ControlCharacter_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("key\x01name"));
    }

    [Fact]
    public void IsValidSegment_NonAscii_ReturnsFalse()
    {
        Assert.False(PluginChannelId.IsValidSegment("kéy"));
    }

    // ── Normalize ─────────────────────────────────────────────────────────

    [Fact]
    public void Normalize_UpperCase_ReturnsLowercased()
    {
        var result = PluginChannelId.Normalize("Terminal");

        Assert.Equal("terminal", result);
    }

    [Fact]
    public void Normalize_WithWhitespace_ReturnsTrimmedLowercased()
    {
        var result = PluginChannelId.Normalize("  MyKey  ");

        Assert.Equal("mykey", result);
    }

    [Fact]
    public void Normalize_Null_ReturnsNull()
    {
        Assert.Null(PluginChannelId.Normalize(null));
    }

    [Fact]
    public void Normalize_InvalidAfterNormalization_ReturnsNull()
    {
        Assert.Null(PluginChannelId.Normalize("key:bad"));
    }
}
