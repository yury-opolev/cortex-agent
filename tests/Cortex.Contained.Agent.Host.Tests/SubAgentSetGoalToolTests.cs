using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// The live control surface: a goal can be aimed, re-aimed and stood down while a run is in
/// flight, and goal state is visible to the main agent without asking the subagent.
/// </summary>
public sealed class SubAgentSetGoalToolTests : IDisposable
{
    private readonly string tempDir;
    private readonly SubagentSessionStore store;

    public SubAgentSetGoalToolTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "setgoal-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(this.tempDir);
        this.store = new SubagentSessionStore(this.tempDir, NullLogger<SubagentSessionStore>.Instance);
    }

    public void Dispose()
    {
        this.store.Dispose();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Execute_SetsGoalAndBudget()
    {
        this.Seed("sa-1");
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-1", goal = "make the build green", maxDuration = "2h", maxContinuations = 50 }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        var task = this.store.GetById("sa-1")!;
        Assert.Equal("make the build green", task.Goal);
        Assert.Equal(TimeSpan.FromHours(2), task.GoalMaxDuration);
        Assert.Equal(50, task.GoalMaxContinuations);
    }

    [Fact]
    public async Task Execute_OmittedGoal_ClearsIt()
    {
        this.Seed("sa-2", goal: "old goal");
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-2" }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(this.store.GetById("sa-2")!.Goal);
    }

    [Fact]
    public async Task Execute_ReAim_DoesNotResetConsumedBudget()
    {
        // Re-aiming a run that has already spent most of its budget must not silently hand it a
        // fresh one — that would make the backstop meaningless for any long run.
        this.Seed("sa-3", goal: "first goal");
        this.store.UpdateGoalBudgetConsumed("sa-3", TimeSpan.FromDays(6), 900);
        var tool = this.NewTool();

        await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-3", goal = "second goal" }),
            Ctx(),
            CancellationToken.None);

        var task = this.store.GetById("sa-3")!;
        Assert.Equal("second goal", task.Goal);
        Assert.Equal(TimeSpan.FromDays(6), task.GoalConsumedElapsed);
        Assert.Equal(900, task.GoalConsumedContinuations);
    }

    [Fact]
    public async Task Execute_FinishedTask_IsRefused()
    {
        this.Seed("sa-4");
        this.store.TrySetTerminalResult("sa-4", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-4", goal = "too late" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Execute_InvalidDuration_ReturnsToolError()
    {
        this.Seed("sa-5");
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-5", goal = "g", maxDuration = "banana" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(this.store.GetById("sa-5")!.Goal);
    }

    [Fact]
    public async Task Execute_UnknownTask_ReturnsToolError()
    {
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "nope", goal = "g" }),
            Ctx(),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Execute_NoBudgetArguments_AppliesAdvertisedDefaults()
    {
        // The schema advertises 7d/10000. Persisting null/null would write a row with no
        // termination backstop, and the next claim would throw out of the runner factory and
        // wedge the task in Running with no runner, permanently, across restarts.
        this.Seed("sa-def");
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-def", goal = "a goal" }),
            Ctx(),
            CancellationToken.None);

        Assert.True(result.Success);
        var task = this.store.GetById("sa-def")!;
        Assert.NotNull(task.GoalMaxDuration);
        Assert.NotNull(task.GoalMaxContinuations);
    }

    [Fact]
    public async Task Execute_TaskOwnedByAnotherConversation_IsRefused()
    {
        // Without an ownership check, any caller holding a task id could re-aim any run in the
        // store — and subagents can now use the sub_agent_* family.
        this.Seed("sa-victim");
        var tool = this.NewTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-victim", goal = "hijacked", maxDuration = "3650d" }),
            new ToolExecutionContext { ChannelId = "subagent-sa-attacker", ConversationId = "subagent-sa-attacker" },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(this.store.GetById("sa-victim")!.Goal);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void Seed(string taskId, string? goal = null) => this.store.Create(new SubagentTask
    {
        TaskId = taskId,
        ParentConversation = "conv-1",
        ParentChannel = "webchat-default",
        Description = "a task",
        Prompt = "do things",
        State = SubagentTaskState.Running,
        Goal = goal,
    });

    private SubAgentSetGoalTool NewTool()
    {
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("m");

        return new SubAgentSetGoalTool(
            this.store,
            new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance),
            Substitute.For<ILlmClient>(),
            modelProvider,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            NullLogger<SubAgentSetGoalTool>.Instance);
    }

    private static ToolExecutionContext Ctx() => new() { ChannelId = "webchat-default", ConversationId = "conv-1" };
}
