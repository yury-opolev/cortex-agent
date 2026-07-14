using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Contracts.Hub;
using ModelContextProtocol;
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

    [Fact]
    public void FromCallException_ProtocolException_ReturnsDefinitiveToolFailure()
    {
        // SAFETY: an McpProtocolException is the SDK's carrier for a JSON-RPC error RESPONSE from
        // the server — the request reached the server and was rejected at the protocol layer BEFORE
        // the tool executed, so the side effect definitively did NOT occur. That stays a definitive
        // Failed/Tool (the agent may deliberately retry after fixing the request).
        var exception = new McpProtocolException("invalid params", McpErrorCode.InvalidParams);

        var mapped = McpResultMapper.FromCallException("inv-1", "github", "create_issue", exception);

        Assert.Equal(McpToolOutcome.Failed, mapped.Outcome);
        Assert.Equal(McpFailureKind.Tool, mapped.FailureKind);
        Assert.Equal("inv-1", mapped.InvocationId);

        // The agent only ever sees the generic, secret-free message — never the raw exception text.
        Assert.Equal(McpErrorSanitizer.ToolFailure("github", "create_issue"), mapped.Error);
        Assert.DoesNotContain("invalid params", mapped.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromCallException_PlainMcpException_ReturnsOutcomeUnknown()
    {
        // SAFETY: any OTHER post-dispatch McpException (session terminated, transport-level, invalid
        // response) is AMBIGUOUS — the request already left the Bridge and MAY have executed. It must
        // NEVER be a definitive Failed (that would invite a retry that double-executes a mutating
        // call); it maps to OutcomeUnknown so callers never auto-retry.
        var exception = new McpException("The server returned HTTP 404; the session has expired.");

        var mapped = McpResultMapper.FromCallException("inv-2", "github", "create_issue", exception);

        Assert.Equal(McpToolOutcome.OutcomeUnknown, mapped.Outcome);
        Assert.Equal(McpFailureKind.Transport, mapped.FailureKind);
        Assert.Equal("inv-2", mapped.InvocationId);
        Assert.NotEqual(McpToolOutcome.Failed, mapped.Outcome);
    }
}
