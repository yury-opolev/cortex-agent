using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using ModelContextProtocol.Protocol;

namespace Cortex.Contained.Bridge.Tests.Mcp;

public sealed class McpResultMapperTests
{
    [Fact]
    public void FlattenContent_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, McpResultMapper.FlattenContent(null));
    }

    [Fact]
    public void FlattenContent_MultipleTextBlocks_JoinedByNewline()
    {
        var content = new ContentBlock[]
        {
            new TextContentBlock { Text = "first" },
            new TextContentBlock { Text = "second" },
        };

        Assert.Equal("first\nsecond", McpResultMapper.FlattenContent(content));
    }

    [Fact]
    public void FlattenContent_NonTextBlock_RendersPlaceholder()
    {
        var content = new ContentBlock[]
        {
            new TextContentBlock { Text = "caption" },
            new ImageContentBlock { Data = new byte[] { 1, 2, 3 }, MimeType = "image/png" },
        };

        var flattened = McpResultMapper.FlattenContent(content);

        Assert.Contains("caption", flattened, StringComparison.Ordinal);
        Assert.Contains("[image content]", flattened, StringComparison.Ordinal);
    }

    [Fact]
    public void ToToolResult_Success_ReturnsOkWithContent()
    {
        var result = new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = "done" }],
        };

        var mapped = McpResultMapper.ToToolResult("inv-1", result);

        Assert.Equal(McpToolOutcome.Succeeded, mapped.Outcome);
        Assert.Equal("inv-1", mapped.InvocationId);
        Assert.False(mapped.IsError);
        Assert.Equal("done", mapped.Content);
    }

    [Fact]
    public void ToToolResult_McpError_ReturnsFailWithContentAsError()
    {
        var result = new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = "boom" }],
        };

        var mapped = McpResultMapper.ToToolResult("inv-1", result);

        // An in-band MCP isError response is a DEFINITIVE tool failure, never an unknown outcome.
        Assert.Equal(McpToolOutcome.Failed, mapped.Outcome);
        Assert.Equal(McpFailureKind.Tool, mapped.FailureKind);
        Assert.True(mapped.IsError);
        Assert.Equal("boom", mapped.Error);
    }
}
