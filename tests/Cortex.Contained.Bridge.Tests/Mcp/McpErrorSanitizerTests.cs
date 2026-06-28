using Cortex.Contained.Bridge.Mcp;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpErrorSanitizerTests
{
    [Fact]
    public void ToolFailure_NamesServerAndTool()
    {
        var message = McpErrorSanitizer.ToolFailure("github", "create_issue");

        Assert.Contains("github", message, StringComparison.Ordinal);
        Assert.Contains("create_issue", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolFailure_DependsOnlyOnServerAndTool_NoExceptionDetailPossible()
    {
        // The signature takes no exception, so no ex.Message (which could carry an endpoint URL with
        // inline credentials) can ever be folded into the agent-facing text.
        var a = McpErrorSanitizer.ToolFailure("srv", "tool");
        var b = McpErrorSanitizer.ToolFailure("srv", "tool");

        Assert.Equal(a, b);
        Assert.DoesNotContain("http", a, StringComparison.OrdinalIgnoreCase);
    }
}
