using System.Globalization;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SubagentSessionStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SubagentSessionStore _store;

    public SubagentSessionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "subagent-store-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new SubagentSessionStore(_tempDir, NullLogger<SubagentSessionStore>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Create & Read ────────────────────────────────────────────────────

    [Fact]
    public void Create_InsertsTask_GetByIdReturnsIt()
    {
        var task = CreateTask("sa-001", "Test task");

        _store.Create(task);

        var retrieved = _store.GetById("sa-001");
        Assert.NotNull(retrieved);
        Assert.Equal("sa-001", retrieved.TaskId);
        Assert.Equal("Test task", retrieved.Description);
        Assert.Equal("Find all TODOs", retrieved.Prompt);
        Assert.Equal(SubagentTaskState.Running, retrieved.State);
        Assert.Equal("conv-1", retrieved.ParentConversation);
        Assert.Equal("webchat-default", retrieved.ParentChannel);
    }

    [Fact]
    public void GetById_NotFound_ReturnsNull()
    {
        var result = _store.GetById("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void Create_WithMessages_PersistsAndDeserializes()
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = "You are a subagent." },
            new() { Role = "user", Content = "Find TODOs." },
            new() { Role = "assistant", Content = "I found 5 TODOs." },
        };
        var task = CreateTask("sa-002", "With messages");
        task.Messages = messages;

        _store.Create(task);

        var retrieved = _store.GetById("sa-002");
        Assert.NotNull(retrieved);
        Assert.Equal(3, retrieved.Messages.Count);
        Assert.Equal("system", retrieved.Messages[0].Role);
        Assert.Equal("You are a subagent.", retrieved.Messages[0].Content);
        Assert.Equal("I found 5 TODOs.", retrieved.Messages[2].Content);
    }

    // ── Active & Parent queries ──────────────────────────────────────────

    [Fact]
    public void GetActive_ReturnsOnlyActiveStates()
    {
        _store.Create(CreateTask("sa-queued", "Queued", SubagentTaskState.Queued));
        _store.Create(CreateTask("sa-running", "Running", SubagentTaskState.Running));
        _store.Create(CreateTask("sa-revising", "Revising", SubagentTaskState.Revising));
        _store.Create(CreateTask("sa-completed", "Completed", SubagentTaskState.Completed));
        _store.Create(CreateTask("sa-failed", "Failed", SubagentTaskState.Failed));

        var active = _store.GetActive();

        Assert.Equal(3, active.Count);
        Assert.Contains(active, t => t.TaskId == "sa-queued");
        Assert.Contains(active, t => t.TaskId == "sa-running");
        Assert.Contains(active, t => t.TaskId == "sa-revising");
    }

    [Fact]
    public void GetByParent_FiltersOnParentConversation()
    {
        _store.Create(CreateTask("sa-a", "Task A", parentConversation: "conv-1"));
        _store.Create(CreateTask("sa-b", "Task B", parentConversation: "conv-2"));
        _store.Create(CreateTask("sa-c", "Task C", parentConversation: "conv-1"));

        var results = _store.GetByParent("conv-1");

        Assert.Equal(2, results.Count);
        Assert.All(results, t => Assert.Equal("conv-1", t.ParentConversation));
    }

    [Fact]
    public void GetOldestQueued_ReturnsEarliestQueuedTask()
    {
        _store.Create(CreateTask("sa-new", "New", SubagentTaskState.Queued));
        _store.Create(CreateTask("sa-running", "Running", SubagentTaskState.Running));

        var oldest = _store.GetOldestQueued();

        Assert.NotNull(oldest);
        Assert.Equal("sa-new", oldest.TaskId);
    }

    [Fact]
    public void GetOldestQueued_NoQueued_ReturnsNull()
    {
        _store.Create(CreateTask("sa-running", "Running", SubagentTaskState.Running));

        var oldest = _store.GetOldestQueued();

        Assert.Null(oldest);
    }

    [Fact]
    public void TryClaimOldestQueued_ClaimsOnce_MarksRunning_SecondCallSkipsIt()
    {
        var now = DateTimeOffset.UtcNow;
        var t1 = new SubagentTask
        {
            TaskId = "sa-q1",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "first",
            Prompt = "p",
            State = SubagentTaskState.Queued,
            CreatedAt = now, // explicit ordering gap so 'first' is unambiguously oldest
        };
        var t2 = new SubagentTask
        {
            TaskId = "sa-q2",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "second",
            Prompt = "p",
            State = SubagentTaskState.Queued,
            CreatedAt = now.AddSeconds(1),
        };
        _store.Create(t1);
        _store.Create(t2);

        var claimedA = _store.TryClaimOldestQueued();
        Assert.NotNull(claimedA);
        Assert.Equal("sa-q1", claimedA!.TaskId);
        Assert.Equal(SubagentTaskState.Running, claimedA.State);
        Assert.Equal(SubagentTaskState.Running, _store.GetById("sa-q1")!.State); // persisted, not just in-memory

        var claimedB = _store.TryClaimOldestQueued();
        Assert.NotNull(claimedB);
        Assert.Equal("sa-q2", claimedB!.TaskId); // NOT sa-q1 again — first claim removed it from the queue

        Assert.Null(_store.TryClaimOldestQueued()); // nothing left queued
    }

    // ── Update state ─────────────────────────────────────────────────────

    [Fact]
    public void UpdateState_ChangesStateAndResult()
    {
        _store.Create(CreateTask("sa-010", "Update test"));

        _store.UpdateState("sa-010", SubagentTaskState.Completed, result: "Done!", evalResponse: "Task completed.");

        var task = _store.GetById("sa-010");
        Assert.NotNull(task);
        Assert.Equal(SubagentTaskState.Completed, task.State);
        Assert.Equal("Done!", task.Result);
        Assert.Equal("Task completed.", task.EvalResponse);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void UpdateState_ToFailed_SetsCompletedAt()
    {
        _store.Create(CreateTask("sa-011", "Fail test"));

        _store.UpdateState("sa-011", SubagentTaskState.Failed, result: "Error occurred");

        var task = _store.GetById("sa-011");
        Assert.NotNull(task);
        Assert.Equal(SubagentTaskState.Failed, task.State);
        Assert.NotNull(task.CompletedAt);
    }

    [Fact]
    public void UpdateState_ToRevising_DoesNotSetCompletedAt()
    {
        _store.Create(CreateTask("sa-012", "Revise test"));

        _store.UpdateState("sa-012", SubagentTaskState.Revising);

        var task = _store.GetById("sa-012");
        Assert.NotNull(task);
        Assert.Equal(SubagentTaskState.Revising, task.State);
        Assert.Null(task.CompletedAt);
    }

    // ── Update messages ──────────────────────────────────────────────────

    [Fact]
    public void UpdateMessages_PersistsNewHistory()
    {
        _store.Create(CreateTask("sa-020", "Messages test"));
        var messages = new List<LlmMessage>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" },
        };

        _store.UpdateMessages("sa-020", messages, rounds: 3);

        var task = _store.GetById("sa-020");
        Assert.NotNull(task);
        Assert.Equal(2, task.Messages.Count);
        Assert.Equal(3, task.Rounds);
    }

    // ── Crash recovery ───────────────────────────────────────────────────

    [Fact]
    public void ResetRunningTasks_MarksRunningAndRevisingAsFailed()
    {
        _store.Create(CreateTask("sa-030", "Running 1", SubagentTaskState.Running));
        _store.Create(CreateTask("sa-031", "Revising 1", SubagentTaskState.Revising));
        _store.Create(CreateTask("sa-032", "Queued 1", SubagentTaskState.Queued));
        _store.Create(CreateTask("sa-033", "Completed 1", SubagentTaskState.Completed));

        var resetCount = _store.ResetRunningTasks();

        Assert.Equal(2, resetCount); // running + revising only
        Assert.Equal(SubagentTaskState.Failed, _store.GetById("sa-030")!.State);
        Assert.Equal(SubagentTaskState.Failed, _store.GetById("sa-031")!.State);
        Assert.Equal(SubagentTaskState.Queued, _store.GetById("sa-032")!.State); // preserved for startup dequeue
        Assert.Equal(SubagentTaskState.Completed, _store.GetById("sa-033")!.State); // untouched
    }

    // ── Cleanup ──────────────────────────────────────────────────────────

    [Fact]
    public void Cleanup_PurgesOldRecords()
    {
        // Create a task that looks old (completed 8 days ago)
        var task = CreateTask("sa-old", "Old task", SubagentTaskState.Completed);
        task.Messages = [new LlmMessage { Role = "user", Content = "test" }];
        _store.Create(task);

        // Manually set completed_at to 8 days ago
        SetCompletedAt("sa-old", DateTimeOffset.UtcNow - TimeSpan.FromDays(8));

        var purged = _store.Cleanup();

        Assert.True(purged > 0);
        Assert.Null(_store.GetById("sa-old"));
    }

    [Fact]
    public void Cleanup_ClearsMessagesAfterRetention()
    {
        var task = CreateTask("sa-medium", "Medium age", SubagentTaskState.Completed);
        task.Messages = [new LlmMessage { Role = "user", Content = "test" }];
        _store.Create(task);

        // Completed 25 hours ago — messages should be cleared but record kept
        SetCompletedAt("sa-medium", DateTimeOffset.UtcNow - TimeSpan.FromHours(25));

        _store.Cleanup();

        var result = _store.GetById("sa-medium");
        Assert.NotNull(result);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void Cleanup_PreservesRecentTasks()
    {
        var task = CreateTask("sa-recent", "Recent task", SubagentTaskState.Completed);
        task.Messages = [new LlmMessage { Role = "user", Content = "test" }];
        _store.Create(task);
        _store.UpdateState("sa-recent", SubagentTaskState.Completed, result: "Done");

        _store.Cleanup();

        var result = _store.GetById("sa-recent");
        Assert.NotNull(result);
        Assert.Single(result.Messages);
    }

    // ── RepointParent ────────────────────────────────────────────────────

    [Fact]
    public void RepointParent_UpdatesBothFields()
    {
        var task = CreateTask("sa-rep-001", "Repoint me");
        _store.Create(task);

        _store.RepointParent("sa-rep-001", "discord-voice-default", "discord-voice");

        var retrieved = _store.GetById("sa-rep-001");
        Assert.NotNull(retrieved);
        Assert.Equal("discord-voice-default", retrieved.ParentConversation);
        Assert.Equal("discord-voice", retrieved.ParentChannel);
    }

    [Fact]
    public void RepointParent_OnlyAffectsSpecifiedTask()
    {
        var t1 = CreateTask("sa-rep-002", "First");
        var t2 = CreateTask("sa-rep-003", "Second");
        _store.Create(t1);
        _store.Create(t2);

        _store.RepointParent("sa-rep-002", "discord-voice-default", "discord-voice");

        var r1 = _store.GetById("sa-rep-002");
        var r2 = _store.GetById("sa-rep-003");
        Assert.NotNull(r1);
        Assert.NotNull(r2);
        Assert.Equal("discord-voice-default", r1.ParentConversation);
        // r2.ParentConversation / r2.ParentChannel must remain whatever CreateTask sets.
        Assert.Equal("conv-1", r2.ParentConversation);
        Assert.Equal("webchat-default", r2.ParentChannel);
    }

    [Fact]
    public void RepointParent_UnknownTaskId_NoOp()
    {
        // Should not throw and should not affect any rows.
        _store.RepointParent("sa-does-not-exist", "discord-voice-default", "discord-voice");

        var retrieved = _store.GetById("sa-does-not-exist");
        Assert.Null(retrieved);
    }

    // ── State serialization round-trip ───────────────────────────────────

    [Theory]
    [InlineData(SubagentTaskState.Queued, "queued")]
    [InlineData(SubagentTaskState.Running, "running")]
    [InlineData(SubagentTaskState.Revising, "revising")]
    [InlineData(SubagentTaskState.Completed, "completed")]
    [InlineData(SubagentTaskState.Failed, "failed")]
    public void SubagentTaskState_RoundTrips(SubagentTaskState state, string expected)
    {
        Assert.Equal(expected, state.ToStorageValue());
        Assert.Equal(state, SubagentTaskStateExtensions.Parse(expected));
    }

    [Fact]
    public void UpdateState_Cancelled_SetsCompletedAt_AndExcludesFromQueue()
    {
        var task = new SubagentTask
        {
            TaskId = "sa-cancel-1",
            ParentConversation = "conv-1",
            ParentChannel = "webchat-default",
            Description = "test",
            Prompt = "do it",
            State = SubagentTaskState.Queued,
        };
        _store.Create(task);

        _store.UpdateState("sa-cancel-1", SubagentTaskState.Cancelled, result: "[Subagent stopped]");

        var reloaded = _store.GetById("sa-cancel-1");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Cancelled, reloaded!.State);
        Assert.NotNull(reloaded.CompletedAt);            // terminal → completed_at stamped
        Assert.Null(_store.GetOldestQueued());            // cancelled is not queued
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SubagentTask CreateTask(
        string taskId,
        string description,
        SubagentTaskState state = SubagentTaskState.Running,
        string parentConversation = "conv-1",
        string parentChannel = "webchat-default")
    {
        return new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = parentConversation,
            ParentChannel = parentChannel,
            Description = description,
            Prompt = "Find all TODOs",
            State = state,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Directly update completed_at in SQLite for testing time-dependent cleanup.</summary>
    private void SetCompletedAt(string taskId, DateTimeOffset completedAt)
    {
        // Access the store's internal state by creating a second connection to the same DB
        var dbPath = Path.Combine(_tempDir, "subagents", "subagents.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE subagent_tasks SET completed_at = $completedAt WHERE task_id = $taskId";
        cmd.Parameters.AddWithValue("$taskId", taskId);
        cmd.Parameters.AddWithValue("$completedAt", completedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
