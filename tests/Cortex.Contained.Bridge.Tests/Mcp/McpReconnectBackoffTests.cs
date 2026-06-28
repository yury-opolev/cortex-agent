using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpReconnectBackoffTests
{
    [Fact]
    public void DelayFor_NonPositiveAttempt_IsZero()
    {
        Assert.Equal(TimeSpan.Zero, McpReconnectBackoff.DelayFor(0));
        Assert.Equal(TimeSpan.Zero, McpReconnectBackoff.DelayFor(-3));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    public void DelayFor_DoublesPerAttempt(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), McpReconnectBackoff.DelayFor(attempt));
    }

    [Fact]
    public void DelayFor_LargeAttempt_CapsAtMaxDelay()
    {
        Assert.Equal(McpReconnectBackoff.MaxDelay, McpReconnectBackoff.DelayFor(20));
        Assert.Equal(McpReconnectBackoff.MaxDelay, McpReconnectBackoff.DelayFor(1000));
    }

    [Fact]
    public void DelayFor_NeverExceedsMaxDelay()
    {
        for (var attempt = 1; attempt <= 64; attempt++)
        {
            Assert.True(McpReconnectBackoff.DelayFor(attempt) <= McpReconnectBackoff.MaxDelay);
        }
    }
}
