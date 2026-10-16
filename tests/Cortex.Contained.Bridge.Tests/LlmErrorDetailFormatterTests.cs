using Cortex.Contained.Bridge.Channels;

namespace Cortex.Contained.Bridge.Tests;

public sealed class LlmErrorDetailFormatterTests
{
    private const string Message = "The model provider returned an error (HTTP 400).";

    [Fact]
    public void Compose_ShowDetailFalse_ReturnsMessageOnly()
    {
        Assert.Equal(Message, LlmErrorDetailFormatter.Compose(Message, "HTTP 400: body", showDetail: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Compose_NoDetail_ReturnsMessageOnly(string? detail)
    {
        Assert.Equal(Message, LlmErrorDetailFormatter.Compose(Message, detail, showDetail: true));
    }

    [Fact]
    public void Compose_ShowDetailWithDetail_AppendsProviderResponse()
    {
        var result = LlmErrorDetailFormatter.Compose(Message, "  HTTP 400: {\"code\":\"model_not_supported\"}  ", showDetail: true);

        Assert.Equal(Message + "\n\nProvider response: HTTP 400: {\"code\":\"model_not_supported\"}", result);
    }

    [Fact]
    public void Compose_LongDetail_TruncatesTo1500WithEllipsis()
    {
        var detail = new string('x', 4000);

        var result = LlmErrorDetailFormatter.Compose(Message, detail, showDetail: true);

        Assert.EndsWith(new string('x', LlmErrorDetailFormatter.MaxDetailLength) + "…", result);
        Assert.DoesNotContain(new string('x', LlmErrorDetailFormatter.MaxDetailLength + 1), result);
    }
}
