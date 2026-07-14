using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tests;

public class ToolCallSummaryTests
{
    [Fact]
    public void TruncateArgs_UnderLimit_ReturnsAsIs()
    {
        Assert.Equal("\"hello\"", ToolCallSummary.TruncateArgs("\"hello\""));
    }

    [Fact]
    public void TruncateArgs_AtLimit_ReturnsAsIs()
    {
        var input = new string('a', 32);
        Assert.Equal(input, ToolCallSummary.TruncateArgs(input));
    }

    [Fact]
    public void TruncateArgs_OverLimit_TruncatesWithEllipsis()
    {
        var input = new string('a', 50);
        var result = ToolCallSummary.TruncateArgs(input);
        Assert.Equal(33, result.Length); // 32 chars + "…"
        Assert.EndsWith("…", result);
        Assert.StartsWith(new string('a', 32), result);
    }

    [Fact]
    public void TruncateArgs_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ToolCallSummary.TruncateArgs(null));
    }

    [Fact]
    public void ToolCallSummary_McpArguments_AreRedacted()
    {
        // An mcp__* tool's raw arguments (which may carry incident content, PII, or telemetry)
        // must never land in the persisted tool-call summary — only the redacted placeholder does.
        var result = ToolCallSummary.TruncateArgs("mcp__github__create_issue", """{"title":"sensitive incident details","body":"secret"}""");

        Assert.Equal("[redacted MCP payload]", result);
    }

    [Fact]
    public void TruncateArgs_WithToolName_BuiltInTool_TruncatesNormally()
    {
        var input = new string('a', 50);
        var result = ToolCallSummary.TruncateArgs("file_read", input);

        Assert.Equal(33, result.Length); // 32 chars + "…"
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void SerializeJson_RoundTripsEntries()
    {
        var entries = new List<ToolCallSummaryEntry>
        {
            new("memory_search", "\"meal\"", true, "before"),
            new("file_read", "\"a.md\"", false, "after"),
        };

        var json = ToolCallSummary.SerializeJson(entries);
        Assert.NotNull(json);
        var parsed = ToolCallSummary.ParseJson(json);

        Assert.Equal(2, parsed.Count);
        Assert.Equal("memory_search", parsed[0].Name);
        Assert.Equal("\"meal\"", parsed[0].Args);
        Assert.True(parsed[0].Ok);
        Assert.Equal("before", parsed[0].Pos);
        Assert.Equal("file_read", parsed[1].Name);
        Assert.False(parsed[1].Ok);
        Assert.Equal("after", parsed[1].Pos);
    }

    [Fact]
    public void SerializeJson_EmptyList_ReturnsNull()
    {
        Assert.Null(ToolCallSummary.SerializeJson([]));
    }

    [Fact]
    public void ParseJson_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(ToolCallSummary.ParseJson(null));
        Assert.Empty(ToolCallSummary.ParseJson(""));
        Assert.Empty(ToolCallSummary.ParseJson("   "));
    }

    [Fact]
    public void ParseJson_Malformed_ReturnsEmpty()
    {
        Assert.Empty(ToolCallSummary.ParseJson("{not json"));
    }

    [Fact]
    public void RenderBlock_Empty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, ToolCallSummary.RenderBlock([]));
    }

    [Fact]
    public void RenderBlock_MixedOkFailures_FormatsLines()
    {
        var entries = new List<ToolCallSummaryEntry>
        {
            new("memory_search", "\"x\"", true, "before"),
            new("file_read", "\"missing.md\"", false, "after"),
        };

        var rendered = ToolCallSummary.RenderBlock(entries);

        Assert.Contains("Tools used (2):", rendered);
        Assert.Contains("- memory_search(\"x\") ✓", rendered);
        Assert.Contains("- file_read(\"missing.md\") ✗", rendered);
    }
}
