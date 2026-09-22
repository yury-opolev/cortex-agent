using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Nested delegation: an autonomous run may subdivide its work. The invariants here are what
/// keep that from deadlocking the shared pool or orphaning work.
/// </summary>
public sealed class NestedDelegationTests : IDisposable
{
    private readonly string tempDir;
    private readonly SubagentSessionStore store;

    public NestedDelegationTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "nested-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(this.tempDir);
        this.store = new SubagentSessionStore(this.tempDir, NullLogger<SubagentSessionStore>.Instance);
    }

    public void Dispose()
    {
        this.store.Dispose();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ── Depth-first claiming: the deadlock fix ───────────────────────

    [Fact]
    public void TryClaimOldestQueued_PrefersDeeperWorkOverOlder()
    {
        // A parent holds its slot while waiting for children, and children are always created
        // AFTER their parents. Pure FIFO therefore admits parents ahead of the children they are
        // waiting on, and a pool full of such parents can never progress. Deepest-first drains
        // the leaves and unwinds the tree.
        this.Seed("sa-parent", depth: 0);
        this.Seed("sa-child", depth: 1, parentTaskId: "sa-parent");

        var claimed = this.store.TryClaimOldestQueued();

        Assert.NotNull(claimed);
        Assert.Equal("sa-child", claimed.TaskId);
    }

    [Fact]
    public void TryClaimOldestQueued_SameDepth_StillOldestFirst()
    {
        this.Seed("sa-first", depth: 0);
        this.Seed("sa-second", depth: 0);

        var claimed = this.store.TryClaimOldestQueued();

        Assert.Equal("sa-first", claimed!.TaskId);
    }

    // ── Depth cap ────────────────────────────────────────────────────

    [Fact]
    public async Task Start_FromSubagentConversation_RecordsParentAndDepth()
    {
        this.Seed("sa-parent", depth: 0, state: SubagentTaskState.Running);
        var tool = this.NewStartTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { description = "child work", prompt = "do the thing" }),
            CtxForSubagent("sa-parent"),
            CancellationToken.None);

        Assert.True(result.Success);
        var child = this.store.GetRecent(10, includeTerminal: true).Single(t => t.TaskId != "sa-parent");
        Assert.Equal("sa-parent", child.ParentTaskId);
        Assert.Equal(1, child.Depth);
    }

    [Fact]
    public async Task Start_BeyondDepthCap_IsRefused()
    {
        this.Seed("sa-deep", depth: SubAgentStartTool.MaxDelegationDepth, state: SubagentTaskState.Running);
        var tool = this.NewStartTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { description = "too deep", prompt = "nope" }),
            CtxForSubagent("sa-deep"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Single(this.store.GetRecent(10, includeTerminal: true));
    }

    [Fact]
    public async Task Start_FromMainAgentConversation_IsDepthZero()
    {
        var tool = this.NewStartTool();

        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { description = "top level", prompt = "go" }),
            new ToolExecutionContext { ChannelId = "webchat-default", ConversationId = "webchat-default" },
            CancellationToken.None);

        Assert.True(result.Success);
        var task = this.store.GetRecent(10, includeTerminal: true).Single();
        Assert.Equal(0, task.Depth);
        Assert.Null(task.ParentTaskId);
    }

    // ── Cascade stop ─────────────────────────────────────────────────

    [Fact]
    public async Task Stop_Parent_CascadesToDescendants()
    {
        // An orphaned child under a multi-day budget could outlive its parent by days.
        this.Seed("sa-root", depth: 0, state: SubagentTaskState.Running);
        this.Seed("sa-mid", depth: 1, parentTaskId: "sa-root", state: SubagentTaskState.Running);
        this.Seed("sa-leaf", depth: 2, parentTaskId: "sa-mid", state: SubagentTaskState.Queued);

        var tool = this.NewStopTool();
        var result = await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-root" }),
            CtxFor("webchat-default"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(SubagentTaskState.Cancelled, this.store.GetById("sa-mid")!.State);
        Assert.Equal(SubagentTaskState.Cancelled, this.store.GetById("sa-leaf")!.State);
    }

    [Fact]
    public async Task Stop_Parent_DoesNotOverwriteAChildThatAlreadyFinished()
    {
        this.Seed("sa-root2", depth: 0, state: SubagentTaskState.Running);
        this.Seed("sa-done", depth: 1, parentTaskId: "sa-root2", state: SubagentTaskState.Running);
        this.store.TrySetTerminalResult("sa-done", new SubagentExecutionResult(SubagentTaskState.Completed, "finished first"));

        var tool = this.NewStopTool();
        await tool.ExecuteAsync(
            JsonSerializer.Serialize(new { task_id = "sa-root2" }),
            CtxFor("webchat-default"),
            CancellationToken.None);

        var done = this.store.GetById("sa-done")!;
        Assert.Equal(SubagentTaskState.Completed, done.State);
        Assert.Equal("finished first", done.Result);
    }

    // ── Schema ───────────────────────────────────────────────────────

    [Fact]
    public void Store_NewDatabase_IsSchemaVersion4()
    {
        using var conn = new SqliteConnection($"Data Source={Path.Combine(this.tempDir, "subagents", "subagents.db")};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";

        Assert.Equal(4, Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void Seed(
        string taskId,
        int depth,
        string? parentTaskId = null,
        SubagentTaskState state = SubagentTaskState.Queued) => this.store.Create(new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = parentTaskId is null ? "webchat-default" : $"subagent-{parentTaskId}",
            ParentChannel = "webchat-default",
            Description = taskId,
            Prompt = "work",
            State = state,
            Depth = depth,
            ParentTaskId = parentTaskId,
        });

    /// <summary>A subagent's own conversation id, which is how the start tool detects nesting.</summary>
    private static ToolExecutionContext CtxForSubagent(string taskId)
        => new() { ChannelId = $"subagent-{taskId}", ConversationId = $"subagent-{taskId}" };

    private SubAgentStartTool NewStartTool() => new(
        this.store,
        this.NewCoordinator(),
        NullLogger<SubAgentStartTool>.Instance);

    private SubAgentStopTool NewStopTool() => new(
        this.store,
        new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance),
        this.NewCoordinator(),
        NullLogger<SubAgentStopTool>.Instance);

    private SubagentExecutionCoordinator NewCoordinator() => new(
        this.store,
        new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance),
        Substitute.For<ISubagentExecutor>(),
        _ => throw new InvalidOperationException("runner factory not used in these tests"),
        new SubagentMessageRouter(
            new AgentMessageChannel(),
            new SubagentRunnerRegistry(5, NullLogger<SubagentRunnerRegistry>.Instance),
            NullLogger<SubagentMessageRouter>.Instance),
        NullLogger<SubagentExecutionCoordinator>.Instance);

    private static ToolExecutionContext CtxFor(string conversationId)
        => new() { ChannelId = "webchat-default", ConversationId = conversationId };
}
