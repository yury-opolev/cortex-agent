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

    [Fact]
    public void McpHubClient_Exposes_CancelMcpTool()
    {
        var method = typeof(IMcpHubClient).GetMethod(nameof(IMcpHubClient.CancelMcpTool));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method!.ReturnType);
        Assert.Equal([typeof(McpToolCancellation)], method.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public void McpHubClient_Exposes_GetMcpActionStatus()
    {
        var method = typeof(IMcpHubClient).GetMethod(nameof(IMcpHubClient.GetMcpActionStatus));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<McpActionStatusResponse>), method!.ReturnType);
        Assert.Equal([typeof(McpActionStatusRequest)], method.GetParameters().Select(p => p.ParameterType).ToArray());
    }

    [Fact]
    public void McpHubClient_Exposes_CancelMcpAction()
    {
        var method = typeof(IMcpHubClient).GetMethod(nameof(IMcpHubClient.CancelMcpAction));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<McpActionCancelResponse>), method!.ReturnType);
        Assert.Equal([typeof(McpActionCancelRequest)], method.GetParameters().Select(p => p.ParameterType).ToArray());
    }
}
