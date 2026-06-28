using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpSecretRefTests
{
    [Fact]
    public void TryParse_SecretToken_ExtractsId()
    {
        var ok = McpSecretRef.TryParse("${secret:mcp/github/apikey}", out var id);

        Assert.True(ok);
        Assert.Equal("mcp/github/apikey", id);
    }

    [Theory]
    [InlineData("literal-value")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("${secret:}")]
    [InlineData("${notsecret:x}")]
    [InlineData("${secret:x")]
    public void TryParse_NonToken_ReturnsFalse(string? value)
    {
        Assert.False(McpSecretRef.TryParse(value, out _));
    }
}
