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

    [Fact]
    public void TransportFailure_NamesServerAndTool_AndExceptionType()
    {
        var message = McpErrorSanitizer.TransportFailure("github", "create_issue", new InvalidOperationException("boom"));

        Assert.Contains("github", message, StringComparison.Ordinal);
        Assert.Contains("create_issue", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransportFailure_NeverContainsTheRawExceptionMessage()
    {
        // SECURITY: this text lands in the admin-facing LastError field (McpServerView.LastError).
        // A raw ex.Message can embed an endpoint URL (possibly with inline credentials), stack
        // fragments, or fragments of an untrusted MCP process's own output.
        var secretLookingMessage = "connection refused at https://user:s3cr3t@internal.example/mcp";

        var message = McpErrorSanitizer.TransportFailure("srv", "tool", new IOException(secretLookingMessage));

        Assert.DoesNotContain(secretLookingMessage, message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", message, StringComparison.Ordinal);
    }
}
