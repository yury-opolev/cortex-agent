using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Mcp;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Hubs;

public sealed class AgentHubMcpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hub-mcp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SubagentSessionStore _store;
    private readonly SubagentExecutionCoordinator _coordinator;

    public AgentHubMcpTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);

        // Not started — UpdateMcpToolCatalog only flips the readiness flag on it.
        _coordinator = new SubagentExecutionCoordinator(
            _store,
            new SubagentRunnerRegistry(1, NullLogger<SubagentRunnerRegistry>.Instance),
            Substitute.For<ISubagentExecutor>(),
            _ => throw new InvalidOperationException("not used"),
            new AgentMessageChannel(),
            NullLogger<SubagentExecutionCoordinator>.Instance);
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

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

    private AgentHub CreateHub(McpToolStore store)
    {
        // The handler is a thin delegate; exercise it without the full hub ctor.
        var hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        SetPrivateField(hub, "mcpToolStore", store);
        SetPrivateField(hub, "logger", NullLogger<AgentHub>.Instance);
        SetPrivateField(hub, "subagentCoordinator", _coordinator);
        return hub;
    }

    [Fact]
    public async Task UpdateMcpToolCatalog_TwoTools_PopulatesStoreAndBumpsVersion()
    {
        var store = new McpToolStore(Substitute.For<IMcpGateway>());
        var hub = CreateHub(store);

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
        var hub = CreateHub(store);

        await hub.UpdateMcpToolCatalog(new McpToolCatalog { Tools = null! });

        Assert.Empty(store.Tools);
        Assert.Equal(2, store.Version);
    }
}
