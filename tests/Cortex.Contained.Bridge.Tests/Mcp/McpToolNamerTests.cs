using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpToolNamerTests
{
    [Fact]
    public void Full_ValidKeyAndTool_ReturnsNamespacedName()
    {
        Assert.Equal("mcp__github__create_issue", McpToolNamer.Full("github", "create_issue"));
    }

    [Fact]
    public void Full_UppercaseKey_IsLowercased()
    {
        Assert.Equal("mcp__github__list", McpToolNamer.Full("GitHub", "list"));
    }

    [Theory]
    [InlineData("bad key")]
    [InlineData("bad.key")]
    [InlineData("")]
    [InlineData("has__double")]
    public void Full_InvalidKey_Throws(string key)
    {
        Assert.Throws<ArgumentException>(() => McpToolNamer.Full(key, "tool"));
    }

    [Fact]
    public void Full_EmptyTool_Throws()
    {
        Assert.Throws<ArgumentException>(() => McpToolNamer.Full("github", ""));
    }

    [Fact]
    public void IsValidServerKey_AllowedCharacters_True()
    {
        Assert.True(McpToolNamer.IsValidServerKey("git-hub_1"));
    }

    [Theory]
    [InlineData("Bad")]
    [InlineData("has space")]
    [InlineData("has__double")]
    [InlineData("")]
    public void IsValidServerKey_Invalid_False(string key)
    {
        Assert.False(McpToolNamer.IsValidServerKey(key));
    }

    [Fact]
    public void TryParse_ValidFullName_ExtractsServerAndTool()
    {
        var ok = McpToolNamer.TryParse("mcp__github__create_issue", out var server, out var tool);

        Assert.True(ok);
        Assert.Equal("github", server);
        Assert.Equal("create_issue", tool);
    }

    [Fact]
    public void TryParse_ToolNameWithDoubleUnderscore_KeepsToolIntact()
    {
        var ok = McpToolNamer.TryParse("mcp__srv__a__b", out var server, out var tool);

        Assert.True(ok);
        Assert.Equal("srv", server);
        Assert.Equal("a__b", tool);
    }

    [Theory]
    [InlineData("create_issue")]
    [InlineData("mcp__github")]
    [InlineData("mcp____tool")]
    [InlineData("mcp__github__")]
    public void TryParse_Invalid_ReturnsFalse(string fullName)
    {
        Assert.False(McpToolNamer.TryParse(fullName, out _, out _));
    }
}
