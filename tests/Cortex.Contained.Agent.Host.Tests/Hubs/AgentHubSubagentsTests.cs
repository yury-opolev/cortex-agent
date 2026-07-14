using System.Reflection;
using System.Runtime.CompilerServices;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests.Hubs;

/// <summary>
/// Proves <see cref="AgentHub.GetSubagentSnapshots"/> is a thin delegate onto
/// <see cref="SubagentObservabilityService"/> — the hub method itself adds no content, it just
/// routes the Bridge → Agent call.
/// </summary>
public sealed class AgentHubSubagentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hub-subagents-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly AgentMetrics _metrics = new();
    private readonly SubagentObservabilityService _observability;
    private readonly AgentHub _hub;

    public AgentHubSubagentsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance);
        _observability = new SubagentObservabilityService(_store, _registry, _metrics, new FakeTimeProvider());

        _hub = (AgentHub)RuntimeHelpers.GetUninitializedObject(typeof(AgentHub));
        SetPrivateField(_hub, "subagentObservability", _observability);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = typeof(AgentHub).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    [Fact]
    public async Task GetSubagentSnapshots_DelegatesToObservabilityService()
    {
        _store.Create(new SubagentTask
        {
            TaskId = "sa-1",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "secret prompt text",
            State = SubagentTaskState.Queued,
        });

        var result = await _hub.GetSubagentSnapshots(new SubagentSnapshotQuery());

        var worker = Assert.Single(result.Workers);
        Assert.Equal("sa-1", worker.TaskId);
        Assert.Equal(1, result.Aggregate.QueueDepth);
    }

    [Fact]
    public async Task GetSubagentSnapshots_NullQuery_UsesDefaults()
    {
        _store.Create(new SubagentTask
        {
            TaskId = "sa-1",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "d",
            Prompt = "p",
            State = SubagentTaskState.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var result = await _hub.GetSubagentSnapshots(null!);

        // IncludeTerminal defaults to true — the completed task is included.
        Assert.Single(result.Workers);
    }
}
