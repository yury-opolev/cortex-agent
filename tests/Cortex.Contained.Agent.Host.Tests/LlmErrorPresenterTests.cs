using Cortex.Contained.Agent.Host.Llm;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Turns a raw provider error message ("HTTP {status}: {body}" or a transport message)
/// into a clean, user-facing one — never leaking a raw HTML/JSON error body (e.g. the
/// GitHub "Unicorn!" 502 page) into a chat channel.
/// </summary>
public class LlmErrorPresenterTests
{
    [Fact]
    public void Http502_HtmlBody_IsSummarizedWithoutHtml()
    {
        var raw = "HTTP 502: <!DOCTYPE html>\n<!-- Hello future GitHubber! -->\n<html><title>Unicorn! · GitHub</title></html>";

        var msg = LlmErrorPresenter.ToUserMessage(raw);

        Assert.Contains("502", msg);
        Assert.DoesNotContain("<", msg);
        Assert.DoesNotContain("GitHubber", msg);
        Assert.DoesNotContain("DOCTYPE", msg);
    }

    [Theory]
    [InlineData("HTTP 503: upstream unavailable")]
    [InlineData("HTTP 504: gateway timeout")]
    public void ServerErrors_MentionTemporary(string raw)
    {
        var msg = LlmErrorPresenter.ToUserMessage(raw);
        Assert.Contains("temporar", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RateLimit_429_IsLabelled()
    {
        var msg = LlmErrorPresenter.ToUserMessage("HTTP 429: rate limit exceeded");
        Assert.Contains("429", msg);
        Assert.Contains("rate", msg, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransportMessage_IsCleanedAndTruncated()
    {
        var raw = "Connection refused <html>noise</html> " + new string('x', 500);

        var msg = LlmErrorPresenter.ToUserMessage(raw);

        Assert.DoesNotContain("<", msg);
        Assert.True(msg.Length < 300);
    }

    [Fact]
    public void NullOrEmpty_YieldsGenericMessage()
    {
        Assert.False(string.IsNullOrWhiteSpace(LlmErrorPresenter.ToUserMessage(null)));
        Assert.False(string.IsNullOrWhiteSpace(LlmErrorPresenter.ToUserMessage("")));
    }
}
