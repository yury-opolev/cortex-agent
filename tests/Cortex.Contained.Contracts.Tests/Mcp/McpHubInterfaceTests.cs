using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Contracts.Tests.Mcp;

public class McpHubInterfaceTests
{
    [Fact]
    public void AgentHub_Composes_McpHub()
    {
        Assert.Contains(typeof(IMcpHub), typeof(IAgentHub).GetInterfaces());
    }

    [Fact]
    public void AgentHubClient_Composes_McpHubClient()
    {
        Assert.Contains(typeof(IMcpHubClient), typeof(IAgentHubClient).GetInterfaces());
    }

    [Fact]
    public void McpHub_Exposes_UpdateMcpToolCatalog()
    {
        Assert.NotNull(typeof(IMcpHub).GetMethod(nameof(IMcpHub.UpdateMcpToolCatalog)));
    }

    [Fact]
    public void McpHubClient_Exposes_InvokeMcpTool()
    {
        Assert.NotNull(typeof(IMcpHubClient).GetMethod(nameof(IMcpHubClient.InvokeMcpTool)));
    }
}
