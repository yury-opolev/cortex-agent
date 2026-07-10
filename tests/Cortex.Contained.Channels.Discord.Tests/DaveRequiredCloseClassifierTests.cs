using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public sealed class DaveRequiredCloseClassifierTests
{
    [Theory]
    [InlineData("Voice", "Voice connection closed: 4017")]
    [InlineData("Voice", "WebSocket closed with code 4017 EndToEndEncryptionDAVEProtocolRequired")]
    [InlineData(null, "Disconnected: close code 4017")]
    public void IsDaveRequired_Close4017_ReturnsTrue(string? source, string message)
    {
        Assert.True(DaveRequiredCloseClassifier.IsDaveRequired(source, message));
    }

    [Theory]
    [InlineData("Gateway", "A task was canceled.")]
    [InlineData("Voice", "Disconnected: 4014")]
    [InlineData("Voice", "")]
    [InlineData("Voice", null)]
    public void IsDaveRequired_Unrelated_ReturnsFalse(string? source, string? message)
    {
        Assert.False(DaveRequiredCloseClassifier.IsDaveRequired(source, message));
    }
}
