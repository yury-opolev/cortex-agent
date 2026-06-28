using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Mcp;

public class SignalRMcpGatewayTests
{
    private sealed class FakeBridgeClientProvider : IBridgeClientProvider
    {
        public IAgentHubClient? Client { get; set; }
    }

    [Fact]
    public async Task InvokeAsync_BridgeConnected_ReturnsBridgeResult()
    {
        var client = Substitute.For<IAgentHubClient>();
        client.InvokeMcpTool(Arg.Any<McpToolInvocation>()).Returns(McpToolResult.Ok("x"));
        var provider = new FakeBridgeClientProvider { Client = client };
        var gateway = new SignalRMcpGateway(provider, new McpGatewayOptions(), NullLogger<SignalRMcpGateway>.Instance);

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", "conv-1", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("x", result.Content);
        await client.Received(1).InvokeMcpTool(Arg.Is<McpToolInvocation>(i =>
            i.ServerKey == "github" && i.ToolName == "create_issue" && i.ArgumentsJson == "{}" && i.ConversationId == "conv-1"));
    }

    [Fact]
    public async Task InvokeAsync_BridgeDisconnected_ReturnsUnreachableFailure_NoThrow()
    {
        var provider = new FakeBridgeClientProvider { Client = null };
        var gateway = new SignalRMcpGateway(provider, new McpGatewayOptions(), NullLogger<SignalRMcpGateway>.Instance);

        var result = await gateway.InvokeAsync("github", "create_issue", "{}", null, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
