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
    };

    private static McpProxyTool BuildTool(IMcpGateway gateway) =>
        new(Definition, gateway, NullLogger<McpProxyTool>.Instance);

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
        var gateway = Substitute.For<IMcpGateway>();
        gateway.InvokeAsync("github", "create_issue", Arg.Any<string>(), "conv-1", Arg.Any<CancellationToken>())
            .Returns(McpToolResult.Ok("hi"));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hi", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_GatewayFail_ReturnsFailureWithError()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.InvokeAsync("github", "create_issue", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(McpToolResult.Fail("boom"));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("boom", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NeedsAuth_ErrorMentionsAuthorization()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.InvokeAsync("github", "create_issue", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(McpToolResult.Fail("token expired", needsAuth: true));
        var tool = BuildTool(gateway);

        var result = await tool.ExecuteAsync("{}", Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("authorization", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("token expired", result.Error);
    }
}
