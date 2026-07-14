using Cortex.Contained.Agent.Host.Mcp;

namespace Cortex.Contained.Agent.Host.Tests.Mcp;

public class McpTelemetrySanitizerTests
{
    [Fact]
    public void Input_McpTool_ReturnsRedactedPlaceholder()
    {
        var result = McpTelemetrySanitizer.Input("mcp__github__create_issue", """{"title":"secret incident details"}""");

        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, result);
    }

    [Fact]
    public void Output_McpTool_ReturnsRedactedPlaceholder()
    {
        var result = McpTelemetrySanitizer.Output("mcp__github__create_issue", "issue #42 created with PII inside");

        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, result);
    }

    [Fact]
    public void Input_BuiltInTool_RemainsUnchanged()
    {
        var result = McpTelemetrySanitizer.Input("file_read", """{"path":"/app/data/notes.md"}""");

        Assert.Equal("""{"path":"/app/data/notes.md"}""", result);
    }

    [Fact]
    public void Output_BuiltInTool_RemainsUnchanged()
    {
        var result = McpTelemetrySanitizer.Output("file_read", "file contents here");

        Assert.Equal("file contents here", result);
    }

    [Fact]
    public void Output_McpTool_NullOutput_ReturnsRedactedPlaceholder()
    {
        // Even a null output (e.g. a failed call with no content) is redacted for an mcp__ tool —
        // never falls through to leak nothing-vs-something as an observable signal.
        var result = McpTelemetrySanitizer.Output("mcp__jira__search", null);

        Assert.Equal(McpTelemetrySanitizer.RedactedPayload, result);
    }

    [Fact]
    public void Output_BuiltInTool_NullOutput_RemainsNull()
    {
        var result = McpTelemetrySanitizer.Output("file_read", null);

        Assert.Null(result);
    }

    [Fact]
    public void Input_ToolNameNotPrefixedWithMcpDunder_RemainsUnchanged()
    {
        // Only an exact "mcp__" prefix is treated as sensitive — a tool that merely contains
        // "mcp" elsewhere in its name is not silently redacted.
        var result = McpTelemetrySanitizer.Input("some_mcp_helper", "plain arguments");

        Assert.Equal("plain arguments", result);
    }
}
