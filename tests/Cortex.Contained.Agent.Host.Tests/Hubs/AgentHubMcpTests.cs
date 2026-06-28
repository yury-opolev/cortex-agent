using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Hubs;

public class AgentHubMcpTests
{
    private static McpToolDefinition Def(string serverKey, string toolName) => new()
    {
        ServerKey = serverKey,
        ToolName = toolName,
        FullName = $"mcp__{serverKey}__{toolName}",
        Description = $"{toolName} description",
        ParametersSchemaJson = """{"type":"object","properties":{}}""",
    };

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = typeof(AgentHub).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    [Fact]
    public async Task UpdateMcpToolCatalog_TwoTools_PopulatesStoreAndBumpsVersion()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());
        // The handler is a thin delegate; exercise it without the full hub ctor.
        var hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        SetPrivateField(hub, "mcpToolStore", store);
        SetPrivateField(hub, "logger", NullLogger<AgentHub>.Instance);

        await hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = [Def("srv", "a"), Def("srv", "b")] });

        Assert.Equal(1, store.Version);
        Assert.Equal(2, store.Tools.Count);
        Assert.True(store.TryGet("mcp__srv__a", out _));
    }

    [Fact]
    public async Task UpdateMcpToolCatalog_NullToolsCatalog_TreatedAsEmpty()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());
        store.Update(new McpToolCatalog { Tools = [Def("srv", "a")] });
        var hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        SetPrivateField(hub, "mcpToolStore", store);
        SetPrivateField(hub, "logger", NullLogger<AgentHub>.Instance);

        await hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = null! });

        Assert.Empty(store.Tools);
        Assert.Equal(2, store.Version);
    }
}
