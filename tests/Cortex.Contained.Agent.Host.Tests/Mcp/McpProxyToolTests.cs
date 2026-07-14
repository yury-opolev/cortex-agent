using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Mcp;

public class McpProxyToolTests
{
    private static readonly McpToolDefinition Definition = new()
    {
        ServerKey = "github",
        ToolName = "create_issue",
        FullName = "mcp__github__create_issue",
        Description = "Create a GitHub issue",
        ParametersSchemaJson = """{"type":"object","properties":{"title":{"type":"string"}}}""",
    };

    private static readonly ToolExecutionContext Context = new()
    {
        ConversationId = "conv-1",
        ChannelId = "webchat-default",
        CorrelationId = "corr-1",
    };

    private static McpProxyTool BuildTool(IMcpGateway gateway) =>
        new(Definition, gateway, NullLogger<McpProxyTool>.Instance);

    private static IMcpGateway GatewayReturning(McpToolResult result)
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);
        return gateway;
    }

    [Fact]
    public void Metadata_MirrorsDefinition()
    {
        var tool = BuildTool(Substitute.For<IMcpGateway>());

        Assert.Equal("mcp__github__create_issue", tool.Name);
        Assert.Equal("Create a GitHub issue", tool.Description);
        Assert.Equal(Definition.ParametersSchemaJson, tool.ParametersSchema);
    }

    [Fact]
    public async Task ExecuteAsync_GatewayOk_ReturnsSuccessWithContent()
    {
        var gateway = GatewayReturning(McpToolResult.Ok("inv-1", "hi"));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);
        await gateway.Received(1).InvokeAsync(
            "github", "create_issue", "{}", "conv-1", "webchat-default", "corr-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_GatewayFail_ReturnsFailureWithError()
    {
        var gateway = GatewayReturning(McpToolResult.Fail("inv-1", McpFailureKind.Tool, "boom"));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NeedsAuth_ErrorMentionsAuthorization()
    {
        var gateway = GatewayReturning(McpToolResult.Fail("inv-1", McpFailureKind.Authentication, "token expired", needsAuth: true));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("authorization", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token expired", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownOutcome_DoesNotRetry_AndWarnsAgainstRepeatingTheCall()
    {
        var gateway = GatewayReturning(McpToolResult.Unknown("inv-1", McpFailureKind.Timeout, "invocation timed out"));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        // CRITICAL: an ambiguous outcome must surface exactly one dispatch (no auto-retry of a
        // potentially mutating call) and the agent-visible error must warn against repeating it.
        Assert.False(result.Success);
        await gateway.Received(1).InvokeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Contains("unknown", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not repeat", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invocation timed out", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledOutcome_ReturnsFailureWithoutRetry()
    {
        var gateway = GatewayReturning(McpToolResult.Cancelled("inv-1", "cancelled before dispatch"));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
        await gateway.Received(1).InvokeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
