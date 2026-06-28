using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Tools;

public class ToolRegistryMcpTests
{
    private static readonly ToolExecutionContext Context = new()
    {
        ConversationId = "conv-1",
        ChannelId = "webchat-default",
    };

    private sealed class StaticTool : IAgentTool
    {
        public string Name => "file_read";
        public string Description => "read a file";
        public string ParametersSchema => """{"type":"object","properties":{}}""";

        public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
            => Task.FromResult(AgentToolResult.Ok("static-result"));
    }

    private static McpToolDefinition Def(string serverKey, string toolName) => new()
    {
        ServerKey = serverKey,
        ToolName = toolName,
        FullName = $"mcp__{serverKey}__{toolName}",
        Description = $"{toolName} description",
        ParametersSchemaJson = """{"type":"object","properties":{}}""",
    };

    [Fact]
    public void GetDefinitions_IncludesMcpTools()
    {
        var gateway = Substitute.For<IMcpGateway>();
        var store = new McpToolStore(gateway);
        store.Update(new McpToolCatalog { Tools = [Def("srv", "alpha")] });
        var registry = new ToolRegistry([new StaticTool()], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance, mcpToolStore: store);

        var defs = registry.GetDefinitions();

        Assert.Contains(defs, d => d.Name == "file_read");
        Assert.Contains(defs, d => d.Name == "mcp__srv__alpha");
    }

    [Fact]
    public async Task ExecuteAsync_McpTool_DispatchesToProxy()
    {
        var gateway = Substitute.For<IMcpGateway>();
        gateway.InvokeAsync("srv", "alpha", Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(McpToolResult.Ok("mcp-result"));
        var store = new McpToolStore(gateway);
        store.Update(new McpToolCatalog { Tools = [Def("srv", "alpha")] });
        var registry = new ToolRegistry([new StaticTool()], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance, mcpToolStore: store);

        var call = new LlmToolCall { Id = "c1", Name = "mcp__srv__alpha", Arguments = "{}" };
        var result = await registry.ExecuteAsync(call, Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("mcp-result", result.Content);
        await gateway.Received(1).InvokeAsync("srv", "alpha", "{}", "conv-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetDefinitions_BumpingStoreVersion_RebuildsCache()
    {
        var gateway = Substitute.For<IMcpGateway>();
        var store = new McpToolStore(gateway);
        store.Update(new McpToolCatalog { Tools = [Def("srv", "alpha")] });
        var registry = new ToolRegistry([new StaticTool()], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance, mcpToolStore: store);

        var first = registry.GetDefinitions();
        Assert.DoesNotContain(first, d => d.Name == "mcp__srv__beta");

        store.Update(new McpToolCatalog { Tools = [Def("srv", "alpha"), Def("srv", "beta")] });
        var second = registry.GetDefinitions();

        Assert.Contains(second, d => d.Name == "mcp__srv__beta");
    }

    [Fact]
    public async Task ExecuteAsync_StaticTool_StillWorks()
    {
        var gateway = Substitute.For<IMcpGateway>();
        var store = new McpToolStore(gateway);
        store.Update(new McpToolCatalog { Tools = [Def("srv", "alpha")] });
        var registry = new ToolRegistry([new StaticTool()], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance, mcpToolStore: store);

        var call = new LlmToolCall { Id = "c1", Name = "file_read", Arguments = "{}" };
        var result = await registry.ExecuteAsync(call, Context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("static-result", result.Content);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownTool_StillFails()
    {
        var gateway = Substitute.For<IMcpGateway>();
        var store = new McpToolStore(gateway);
        var registry = new ToolRegistry([new StaticTool()], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance, mcpToolStore: store);

        var call = new LlmToolCall { Id = "c1", Name = "mcp__srv__missing", Arguments = "{}" };
        var result = await registry.ExecuteAsync(call, Context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown tool", result.Error);
    }
}
