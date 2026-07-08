using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class SubAgentStopToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sastop-" + Guid.NewGuid().ToString("N"));
    private readonly SubagentSessionStore _store;
    private readonly SubagentRunnerRegistry _registry;
    private readonly SubAgentStopTool _tool;

    public SubAgentStopToolTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        _tool = new SubAgentStopTool(_store, _registry, NullLogger<SubAgentStopTool>.Instance);
    }

    private SubagentTask Seed(string id, SubagentTaskState state)
    {
        var t = new SubagentTask
        {
            TaskId = id, ParentConversation = "c", ParentChannel = "webchat-default",
            Description = "d", Prompt = "p", State = state,
        };
        _store.Create(t);
        if (state != SubagentTaskState.Queued)
        {
            _store.UpdateState(id, state);
        }
        return t;
    }

    private static ToolExecutionContext Ctx() => new()
    {
        ConversationId = "c", ChannelId = "webchat-default",
    };

    private static SubagentRunner Runner()
    {
        var client = Substitute.For<ILlmClient>();
        var reg = new ToolRegistry([], new ActiveChannelStore(), NullLogger<ToolRegistry>.Instance);
        return new SubagentRunner(client, reg, 10, NullLogger<SubagentRunner>.Instance);
    }

    [Fact]
    public async Task Stop_RunningRegistered_CancelsToken_ReturnsOk()
    {
        Seed("sa-1", SubagentTaskState.Running);
        _registry.TryRegister("sa-1", Runner());
        var token = _registry.GetCancellationToken("sa-1");

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-1"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Stop_Queued_MarksCancelled()
    {
        Seed("sa-2", SubagentTaskState.Queued);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-2"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Cancelled, _store.GetById("sa-2")!.State);
    }

    [Fact]
    public async Task Stop_Unknown_Fails()
    {
        var result = await _tool.ExecuteAsync("""{"task_id":"nope"}""", Ctx(), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Stop_AlreadyCompleted_ReportsNoChange()
    {
        Seed("sa-3", SubagentTaskState.Completed);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-3"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Completed, _store.GetById("sa-3")!.State);
    }

    [Fact]
    public async Task Stop_MissingTaskId_Fails()
    {
        var result = await _tool.ExecuteAsync("""{}""", Ctx(), CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Stop_RunningButNoLiveRunner_MarksCancelledDefensively()
    {
        // State says Running, but no runner is registered (mid-transition window):
        // TryCancel returns false, so the tool marks the task Cancelled defensively.
        Seed("sa-5", SubagentTaskState.Running);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-5"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Cancelled, _store.GetById("sa-5")!.State);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
