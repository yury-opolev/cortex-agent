using Cortex.Contained.Bridge.Setup;

namespace Cortex.Contained.Bridge.Tests.Setup;

public sealed class GitHubErrorParserTests
{
    // --- Null / empty / whitespace ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_NullOrWhitespace_ReturnsNull(string? input)
    {
        var result = GitHubErrorParser.Describe(input);

        Assert.Null(result);
    }

    // --- Malformed / non-JSON ---

    [Fact]
    public void Describe_HtmlBody_ReturnsNull()
    {
        var result = GitHubErrorParser.Describe("<html>400</html>");

        Assert.Null(result);
    }

    // --- Valid JSON, no error fields ---

    [Fact]
    public void Describe_EmptyJsonObject_ReturnsNull()
    {
        var result = GitHubErrorParser.Describe("{}");

        Assert.Null(result);
    }

    // --- error + error_description ---

    [Fact]
    public void Describe_BothFields_ContainsDescriptionAndCode()
    {
        const string body = """{"error":"device_flow_disabled","error_description":"Device flow is disabled for this app."}""";

        var result = GitHubErrorParser.Describe(body);

        Assert.NotNull(result);
        Assert.Contains("Device flow is disabled for this app.", result, StringComparison.Ordinal);
        Assert.Contains("device_flow_disabled", result, StringComparison.Ordinal);
    }

    // --- error only (no description) ---

    [Fact]
    public void Describe_ErrorCodeOnly_ReturnsCode()
    {
        const string body = """{"error":"slow_down"}""";

        var result = GitHubErrorParser.Describe(body);

        Assert.Equal("slow_down", result);
    }

    // --- non-object JSON root: must return null, never throw (proxy/gateway edge case) ---

    [Theory]
    [InlineData("123")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("\"just a message\"")]
    public void Describe_NonObjectJsonRoot_ReturnsNull(string body)
    {
        Assert.Null(GitHubErrorParser.Describe(body));
    }
}
