using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.Mcp;

public class McpToolStoreTests
{
    private static McpToolDefinition Def(string serverKey, string toolName) => new()
    {
        ServerKey = serverKey,
        ToolName = toolName,
        FullName = $"mcp__{serverKey}__{toolName}",
        Description = $"{toolName} description",
        ParametersSchemaJson = """{"type":"object","properties":{}}""",
    };

    [Fact]
    public void EmptyStore_HasVersionZero_AndNoTools()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());

        Assert.Equal(0, store.Version);
        Assert.Empty(store.Tools);
        Assert.False(store.TryGet("mcp__srv__a", out _));
    }

    [Fact]
    public void Update_WithTwoDefinitions_RegistersBothAndBumpsVersion()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());

        store.Update(new McpToolCatalog { Tools = [Def("srv", "a"), Def("srv", "b")] });

        Assert.Equal(1, store.Version);
        Assert.Equal(2, store.Tools.Count);
        Assert.True(store.TryGet("mcp__srv__a", out var tool));
        Assert.NotNull(tool);
        Assert.Equal("mcp__srv__a", tool.Name);
    }

    [Fact]
    public void TryGet_IsCaseInsensitive()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());
        store.Update(new McpToolCatalog { Tools = [Def("srv", "a")] });

        Assert.True(store.TryGet("MCP__SRV__A", out var tool));
        Assert.NotNull(tool);
    }

    [Fact]
    public void Update_WithEmptyCatalog_ClearsTools_AndBumpsVersionAgain()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());
        store.Update(new McpToolCatalog { Tools = [Def("srv", "a"), Def("srv", "b")] });

        store.Update(new McpToolCatalog());

        Assert.Equal(2, store.Version);
        Assert.Empty(store.Tools);
        Assert.False(store.TryGet("mcp__srv__a", out _));
    }
}
