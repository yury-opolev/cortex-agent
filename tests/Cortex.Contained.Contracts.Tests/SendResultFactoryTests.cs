using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Contracts.Tests;

public sealed class SendResultFactoryTests
{
    [Fact]
    public void Ok_SetsSuccess_OptionalExternalId()
    {
        Assert.True(SendResult.Ok().Success);
        Assert.Null(SendResult.Ok().ExternalMessageId);
        Assert.Equal("x", SendResult.Ok("x").ExternalMessageId);
    }

    [Fact]
    public void Error_SetsFailureAndMessage()
    {
        var r = SendResult.Error("nope");
        Assert.False(r.Success);
        Assert.Equal("nope", r.ErrorMessage);
    }
}
