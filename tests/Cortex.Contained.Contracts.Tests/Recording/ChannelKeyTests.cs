using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Contracts.Tests.Recording;

public class ChannelKeyTests
{
    [Fact]
    public void ForDiscord_LowercasesPrefix()
        => Assert.Equal("discord:123", ChannelKey.ForDiscord(123));

    [Fact]
    public void Host_IsLiteral()
        => Assert.Equal("host", ChannelKey.Host);

    [Theory]
    [InlineData("discord:1")]
    [InlineData("discord:18446744073709551615")] // ulong.MaxValue
    [InlineData("host")]
    public void IsValid_AcceptsKnownShapes(string key)
        => Assert.True(ChannelKey.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DISCORD:1")]
    [InlineData("foo")]
    [InlineData("discord:")]
    [InlineData("discord:abc")]
    [InlineData("discord:-1")]
    public void IsValid_RejectsUnknown(string? key)
        => Assert.False(ChannelKey.IsValid(key));
}
