using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpEnvSecretHeuristicTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("node")]
    [InlineData("production")]
    [InlineData("info")]
    [InlineData("${secret:my-api-key}")]                            // proper secret token
    [InlineData("https://api.example.com/v1/some/long/endpoint")]   // URL/endpoint, not a secret
    [InlineData("a normal long sentence value with many spaces")]   // has whitespace
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]                // long but zero entropy
    public void LooksLikeSecret_NonSecretValues_ReturnFalse(string? value)
    {
        Assert.False(McpEnvSecretHeuristic.LooksLikeSecret(value));
    }

    [Theory]
    [InlineData("ghp_aB3kLm9QwErTy7zX1cVbN8mZ2pK0jH4dF6gS")]
    [InlineData("sk-Zm9vYmFyYmF6cXV4MTIzNDU2Nzg5MGFiY2RlZmdo")]
    [InlineData("AKIA4XQ7P2WJ9ZK3MN8Lq1rS5tU7vW2xY0bC6dE")]
    public void LooksLikeSecret_HighEntropyLongTokens_ReturnTrue(string value)
    {
        Assert.True(McpEnvSecretHeuristic.LooksLikeSecret(value));
    }
}
