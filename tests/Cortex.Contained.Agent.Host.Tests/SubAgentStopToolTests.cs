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
    private readonly SubagentExecutionCoordinator _coordinator;
    private readonly SubAgentStopTool _tool;

    public SubAgentStopToolTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SubagentSessionStore(_dir, NullLogger<SubagentSessionStore>.Instance);
        _registry = new SubagentRunnerRegistry(2, NullLogger<SubagentRunnerRegistry>.Instance);
        _coordinator = new SubagentExecutionCoordinator(
            _store,
            _registry,
            new NoopExecutor(),
            _ => Runner(),
            new AgentMessageChannel(),
            NullLogger<SubagentExecutionCoordinator>.Instance);
        _tool = new SubAgentStopTool(_store, _registry, _coordinator, NullLogger<SubAgentStopTool>.Instance);
    }

    private SubagentTask Seed(string id, SubagentTaskState state)
    {
        // Create persists the requested state directly (terminal states included), so no separate
        // state-setter is needed for the fixture.
        var t = new SubagentTask
        {
            TaskId = id, ParentConversation = "c", ParentChannel = "webchat-default",
            Description = "d", Prompt = "p", State = state,
        };
        _store.Create(t);
        return t;
    }

    private sealed class NoopExecutor : ISubagentExecutor
    {
        public Task<SubagentExecutionResult> ExecuteAsync(SubagentTask task, CancellationToken cancellationToken)
            => Task.FromResult(new SubagentExecutionResult(SubagentTaskState.Completed, "done"));
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
        _registry.TryRegister("sa-1", Runner(), out var token);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-1"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Stop_Running_CancelsRegistryToken()
    {
        Seed("sa-run", SubagentTaskState.Running);
        _registry.TryRegister("sa-run", Runner(), out var registryToken);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-run"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        // The tool cancels ONLY the registry-owned token; the coordinator then records Cancelled.
        Assert.True(registryToken.IsCancellationRequested);
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
    public async Task Stop_Queued_CreatesCancelledTerminalResult()
    {
        Seed("sa-q", SubagentTaskState.Queued);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-q"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        var task = _store.GetById("sa-q")!;
        // Guarded terminal transition: Cancelled (never Completed) with a pending notification.
        Assert.Equal(SubagentTaskState.Cancelled, task.State);
        Assert.NotNull(task.CompletedAt);
        Assert.Equal(SubagentNotificationState.Pending, task.NotificationState);
        Assert.Null(_store.GetOldestQueued()); // no longer queued
    }

    [Fact]
    public async Task Stop_Queued_AlsoCancelsRegisteredRunner_ClosingTheClaimRace()
    {
        // Race: the coordinator registered a runner for this task between the tool's state read
        // and its terminal write. The queued-cancel path must ALSO cancel the registry token so
        // the just-registered runner doesn't execute in a wasted slot.
        Seed("sa-race", SubagentTaskState.Queued);
        _registry.TryRegister("sa-race", Runner(), out var token);

        var result = await _tool.ExecuteAsync("""{"task_id":"sa-race"}""", Ctx(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Cancelled, _store.GetById("sa-race")!.State);
        Assert.True(token.IsCancellationRequested);
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
        _coordinator.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }
}
