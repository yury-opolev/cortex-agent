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

    // ── Recent (observability paging) ────────────────────────────────────

    [Fact]
    public void GetRecent_IncludeTerminalTrue_ReturnsAllStatesNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        _store.Create(CreateTask("sa-r1", "First", SubagentTaskState.Queued, createdAt: now));
        _store.Create(CreateTask("sa-r2", "Second", SubagentTaskState.Completed, createdAt: now.AddSeconds(1)));
        _store.Create(CreateTask("sa-r3", "Third", SubagentTaskState.Running, createdAt: now.AddSeconds(2)));

        var recent = _store.GetRecent(limit: 100, includeTerminal: true);

        Assert.Equal(3, recent.Count);
        Assert.Equal("sa-r3", recent[0].TaskId); // newest first
        Assert.Equal("sa-r2", recent[1].TaskId);
        Assert.Equal("sa-r1", recent[2].TaskId);
    }

    [Fact]
    public void GetRecent_IncludeTerminalFalse_ExcludesTerminalStates()
    {
        _store.Create(CreateTask("sa-active", "Active", SubagentTaskState.Running));
        _store.Create(CreateTask("sa-done", "Done", SubagentTaskState.Completed));

        var recent = _store.GetRecent(limit: 100, includeTerminal: false);

        Assert.Single(recent);
        Assert.Equal("sa-active", recent[0].TaskId);
    }

    [Fact]
    public void GetRecent_RespectsLimit()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            _store.Create(CreateTask($"sa-lim-{i}", $"Task {i}", SubagentTaskState.Queued, createdAt: now.AddSeconds(i)));
        }

        var recent = _store.GetRecent(limit: 2, includeTerminal: true);

        Assert.Equal(2, recent.Count);
        Assert.Equal("sa-lim-4", recent[0].TaskId);
        Assert.Equal("sa-lim-3", recent[1].TaskId);
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
    public void RecoverInterruptedWork_RequeuesRunningAndRevising()
    {
        _store.Create(CreateTask("sa-030", "Running 1", SubagentTaskState.Running));
        _store.Create(CreateTask("sa-031", "Revising 1", SubagentTaskState.Revising));
        _store.Create(CreateTask("sa-032", "Queued 1", SubagentTaskState.Queued));
        _store.Create(CreateTask("sa-033", "Completed 1", SubagentTaskState.Completed));

        var recovered = _store.RecoverInterruptedWork();

        Assert.Equal(2, recovered); // running + revising only
        var requeuedRunning = _store.GetById("sa-030");
        var requeuedRevising = _store.GetById("sa-031");
        Assert.NotNull(requeuedRunning);
        Assert.NotNull(requeuedRevising);
        Assert.Equal(SubagentTaskState.Queued, requeuedRunning.State);
        Assert.Equal(SubagentTaskState.Queued, requeuedRevising.State);
        Assert.Null(requeuedRunning.CompletedAt);
        Assert.Equal(1, requeuedRunning.RestartCount);
        Assert.Equal(SubagentTaskState.Queued, _store.GetById("sa-032")!.State); // untouched
        Assert.Equal(0, _store.GetById("sa-032")!.RestartCount);
        Assert.Equal(SubagentTaskState.Completed, _store.GetById("sa-033")!.State); // untouched
    }

    [Fact]
    public void RecoverInterruptedWork_UsesResumeModeWhenMessagesExist()
    {
        var withMessages = CreateTask("sa-034", "Has history", SubagentTaskState.Running);
        withMessages.Messages = [new LlmMessage { Role = "user", Content = "step 1" }];
        _store.Create(withMessages);
        _store.Create(CreateTask("sa-035", "No history", SubagentTaskState.Running));

        _store.RecoverInterruptedWork();

        Assert.Equal(SubagentRunMode.Resume, _store.GetById("sa-034")!.RunMode);
        Assert.Equal(SubagentRunMode.New, _store.GetById("sa-035")!.RunMode);
    }

    [Fact]
    public void RecoverInterruptedWork_PreservesTerminalStates()
    {
        var completed = CreateTask("sa-036", "Done", SubagentTaskState.Completed);
        completed.CompletedAt = DateTimeOffset.UtcNow;
        completed.NotificationState = SubagentNotificationState.Delivered;
        var failed = CreateTask("sa-037", "Broken", SubagentTaskState.Failed);
        failed.CompletedAt = DateTimeOffset.UtcNow;
        failed.NotificationState = SubagentNotificationState.Delivered;
        var cancelled = CreateTask("sa-038", "Stopped", SubagentTaskState.Cancelled);
        cancelled.CompletedAt = DateTimeOffset.UtcNow;
        cancelled.NotificationState = SubagentNotificationState.Delivered;
        _store.Create(completed);
        _store.Create(failed);
        _store.Create(cancelled);

        var recovered = _store.RecoverInterruptedWork();

        Assert.Equal(0, recovered);
        Assert.Equal(SubagentTaskState.Completed, _store.GetById("sa-036")!.State);
        Assert.Equal(SubagentTaskState.Failed, _store.GetById("sa-037")!.State);
        Assert.Equal(SubagentTaskState.Cancelled, _store.GetById("sa-038")!.State);
        Assert.NotNull(_store.GetById("sa-036")!.CompletedAt);
    }

    [Fact]
    public void RecoverInterruptedWork_ReleasesEnqueuedNotifications()
    {
        var task = CreateTask("sa-039", "Claimed but undelivered", SubagentTaskState.Completed);
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.NotificationState = SubagentNotificationState.Enqueued;
        task.NotificationAttempts = 1;
        _store.Create(task);

        var recovered = _store.RecoverInterruptedWork();

        Assert.Equal(1, recovered);
        var reloaded = _store.GetById("sa-039");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Completed, reloaded.State); // still terminal
        Assert.Equal(SubagentNotificationState.Pending, reloaded.NotificationState); // retryable again
    }

    // ── Resume queueing ──────────────────────────────────────────────────

    [Fact]
    public void TryQueueResume_AppendsMessageAndQueuesResume()
    {
        var task = CreateTask("sa-040", "Resumable", SubagentTaskState.Completed);
        task.Messages =
        [
            new LlmMessage { Role = "user", Content = "original" },
            new LlmMessage { Role = "assistant", Content = "done" },
        ];
        task.CompletedAt = DateTimeOffset.UtcNow;
        task.NotificationState = SubagentNotificationState.Delivered;
        _store.Create(task);

        var queued = _store.TryQueueResume("sa-040", "keep going");

        Assert.True(queued);
        var reloaded = _store.GetById("sa-040");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Queued, reloaded.State);
        Assert.Equal(SubagentRunMode.Resume, reloaded.RunMode);
        Assert.Null(reloaded.CompletedAt);
        Assert.Equal(3, reloaded.Messages.Count);
        Assert.Equal("user", reloaded.Messages[2].Role);
        Assert.Equal("keep going", reloaded.Messages[2].Content);
        Assert.Equal(SubagentNotificationState.None, reloaded.NotificationState);
    }

    [Fact]
    public void TryQueueResume_UnknownTask_ReturnsFalse()
    {
        Assert.False(_store.TryQueueResume("sa-missing", "hello"));
    }

    // ── Terminal results ─────────────────────────────────────────────────

    [Fact]
    public void TrySetTerminalResult_FailedCannotBeOverwrittenByCompleted()
    {
        _store.Create(CreateTask("sa-050", "Fails first", SubagentTaskState.Running));

        var failedApplied = _store.TrySetTerminalResult(
            "sa-050", new SubagentExecutionResult(SubagentTaskState.Failed, "boom"));
        var completedApplied = _store.TrySetTerminalResult(
            "sa-050", new SubagentExecutionResult(SubagentTaskState.Completed, "all good"));

        Assert.True(failedApplied);
        Assert.False(completedApplied);
        var reloaded = _store.GetById("sa-050");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Failed, reloaded.State);
        Assert.Equal("boom", reloaded.Result);
    }

    [Fact]
    public void TrySetTerminalResult_CancelledCannotBeOverwrittenByCompleted()
    {
        _store.Create(CreateTask("sa-051", "Cancelled first", SubagentTaskState.Running));

        var cancelledApplied = _store.TrySetTerminalResult(
            "sa-051", new SubagentExecutionResult(SubagentTaskState.Cancelled, "[stopped]"));
        var completedApplied = _store.TrySetTerminalResult(
            "sa-051", new SubagentExecutionResult(SubagentTaskState.Completed, "all good"));

        Assert.True(cancelledApplied);
        Assert.False(completedApplied);
        var reloaded = _store.GetById("sa-051");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Cancelled, reloaded.State);
        Assert.Equal("[stopped]", reloaded.Result);
    }

    [Fact]
    public void TrySetTerminalResult_CreatesPendingNotification()
    {
        _store.Create(CreateTask("sa-052", "Completes", SubagentTaskState.Running));

        var applied = _store.TrySetTerminalResult(
            "sa-052", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));

        Assert.True(applied);
        var reloaded = _store.GetById("sa-052");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Completed, reloaded.State);
        Assert.Equal("done", reloaded.Result);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.Equal(SubagentNotificationState.Pending, reloaded.NotificationState);
        Assert.NotNull(reloaded.NotificationUpdatedAt);
    }

    [Fact]
    public void TrySetTerminalResult_NonTerminalState_Throws()
    {
        _store.Create(CreateTask("sa-053", "Guarded", SubagentTaskState.Running));

        Assert.Throws<ArgumentException>(() => _store.TrySetTerminalResult(
            "sa-053", new SubagentExecutionResult(SubagentTaskState.Running, "nope")));
    }

    // ── Requeue ──────────────────────────────────────────────────────────

    [Fact]
    public void Requeue_SetsQueuedClearsCompletedAtAndIncrementsRestartCount()
    {
        var task = CreateTask("sa-054", "Requeue me", SubagentTaskState.Running);
        _store.Create(task);

        var requeued = _store.Requeue("sa-054");

        Assert.True(requeued);
        var reloaded = _store.GetById("sa-054");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Queued, reloaded.State);
        Assert.Null(reloaded.CompletedAt);
        Assert.Equal(1, reloaded.RestartCount);
    }

    [Fact]
    public void Requeue_TerminalCancelledTask_IsNotResurrected()
    {
        _store.Create(CreateTask("sa-055", "Cancelled stays cancelled", SubagentTaskState.Running));
        _store.TrySetTerminalResult(
            "sa-055", new SubagentExecutionResult(SubagentTaskState.Cancelled, "[stopped]"));

        var requeued = _store.Requeue("sa-055");

        Assert.False(requeued);
        var reloaded = _store.GetById("sa-055");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Cancelled, reloaded.State); // NOT resurrected to queued
        Assert.Equal("[stopped]", reloaded.Result);
        Assert.NotNull(reloaded.CompletedAt);
        Assert.Equal(0, reloaded.RestartCount);
    }

    // ── Notification queue ───────────────────────────────────────────────

    [Fact]
    public void TryClaimOldestPendingNotification_ClaimsOnce()
    {
        var now = DateTimeOffset.UtcNow;
        var first = CreateTask("sa-060", "First done", SubagentTaskState.Running, createdAt: now);
        var second = CreateTask("sa-061", "Second done", SubagentTaskState.Running, createdAt: now.AddSeconds(1));
        _store.Create(first);
        _store.Create(second);
        _store.TrySetTerminalResult("sa-060", new SubagentExecutionResult(SubagentTaskState.Completed, "a"));
        _store.TrySetTerminalResult("sa-061", new SubagentExecutionResult(SubagentTaskState.Completed, "b"));

        var claimedFirst = _store.TryClaimOldestPendingNotification();
        var claimedSecond = _store.TryClaimOldestPendingNotification();
        var claimedThird = _store.TryClaimOldestPendingNotification();

        Assert.NotNull(claimedFirst);
        Assert.Equal("sa-060", claimedFirst.TaskId);
        Assert.Equal(SubagentNotificationState.Enqueued, claimedFirst.NotificationState);
        Assert.Equal(1, claimedFirst.NotificationAttempts);
        Assert.NotNull(claimedSecond);
        Assert.Equal("sa-061", claimedSecond.TaskId);
        Assert.Null(claimedThird); // both already enqueued — nothing pending
        Assert.Equal(SubagentNotificationState.Enqueued, _store.GetById("sa-060")!.NotificationState); // persisted
    }

    [Fact]
    public void ReleaseNotification_MakesClaimRetryable()
    {
        _store.Create(CreateTask("sa-062", "Retryable", SubagentTaskState.Running));
        _store.TrySetTerminalResult("sa-062", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));

        var claimed = _store.TryClaimOldestPendingNotification();
        Assert.NotNull(claimed);
        Assert.Null(_store.TryClaimOldestPendingNotification()); // enqueued → not claimable

        var released = _store.ReleaseNotification("sa-062");

        Assert.True(released);
        var reclaimed = _store.TryClaimOldestPendingNotification();
        Assert.NotNull(reclaimed);
        Assert.Equal("sa-062", reclaimed.TaskId);
        Assert.Equal(2, reclaimed.NotificationAttempts);
    }

    [Fact]
    public void MarkNotificationDelivered_RemovesFromPendingQuery()
    {
        _store.Create(CreateTask("sa-063", "Deliverable", SubagentTaskState.Running));
        _store.TrySetTerminalResult("sa-063", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));

        var delivered = _store.MarkNotificationDelivered("sa-063");

        Assert.True(delivered);
        Assert.Null(_store.TryClaimOldestPendingNotification());
        Assert.Equal(SubagentNotificationState.Delivered, _store.GetById("sa-063")!.NotificationState);
    }

    // ── Progress & transfer queries ──────────────────────────────────────

    [Fact]
    public void TouchProgress_UpdatesLastProgressAt()
    {
        var task = CreateTask("sa-064", "Progressing", SubagentTaskState.Running);
        task.LastProgressAt = DateTimeOffset.UtcNow.AddHours(-1);
        _store.Create(task);

        _store.TouchProgress("sa-064");

        var reloaded = _store.GetById("sa-064");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.LastProgressAt > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void GetTransferableTasks_IncludesActiveAndUndeliveredTerminal()
    {
        _store.Create(CreateTask("sa-070", "Queued", SubagentTaskState.Queued));
        _store.Create(CreateTask("sa-071", "Running", SubagentTaskState.Running));
        _store.Create(CreateTask("sa-072", "Undelivered", SubagentTaskState.Running));
        _store.TrySetTerminalResult("sa-072", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));
        var delivered = CreateTask("sa-073", "Delivered", SubagentTaskState.Completed);
        delivered.CompletedAt = DateTimeOffset.UtcNow;
        delivered.NotificationState = SubagentNotificationState.Delivered;
        _store.Create(delivered);

        var transferable = _store.GetTransferableTasks();

        Assert.Equal(3, transferable.Count);
        Assert.Contains(transferable, t => t.TaskId == "sa-070");
        Assert.Contains(transferable, t => t.TaskId == "sa-071");
        Assert.Contains(transferable, t => t.TaskId == "sa-072"); // terminal but result not yet delivered
        Assert.DoesNotContain(transferable, t => t.TaskId == "sa-073");
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
        var task = CreateTask("sa-recent", "Recent task", SubagentTaskState.Running);
        task.Messages = [new LlmMessage { Role = "user", Content = "test" }];
        _store.Create(task);
        _store.TrySetTerminalResult("sa-recent", new SubagentExecutionResult(SubagentTaskState.Completed, "Done"));

        _store.Cleanup();

        var result = _store.GetById("sa-recent");
        Assert.NotNull(result);
        Assert.Single(result.Messages);
    }

    [Fact]
    public void Cleanup_PreservesUndeliveredTerminalTask()
    {
        _store.Create(CreateTask("sa-undelivered", "Terminal, not delivered", SubagentTaskState.Running));
        _store.TrySetTerminalResult(
            "sa-undelivered", new SubagentExecutionResult(SubagentTaskState.Completed, "important result"));
        SetCompletedAt("sa-undelivered", DateTimeOffset.UtcNow - TimeSpan.FromDays(8));

        _store.Cleanup();

        // Notification still pending — the record must survive even past record retention.
        var survivor = _store.GetById("sa-undelivered");
        Assert.NotNull(survivor);
        Assert.Equal("important result", survivor.Result);

        // Once delivered, the same record becomes eligible for cleanup.
        Assert.True(_store.MarkNotificationDelivered("sa-undelivered"));
        _store.Cleanup();
        Assert.Null(_store.GetById("sa-undelivered"));
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

    [Fact]
    public void TrySetTerminalResult_Cancelled_SetsCompletedAt_AndExcludesFromQueue()
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

        // The guarded terminal write is the queued-cancel path used by sub_agent_stop.
        _store.TrySetTerminalResult(
            "sa-cancel-1", new SubagentExecutionResult(SubagentTaskState.Cancelled, "[Subagent stopped]"));

        var reloaded = _store.GetById("sa-cancel-1");
        Assert.NotNull(reloaded);
        Assert.Equal(SubagentTaskState.Cancelled, reloaded!.State);
        Assert.NotNull(reloaded.CompletedAt);            // terminal → completed_at stamped
        Assert.Null(_store.GetOldestQueued());            // cancelled is not queued
    }

    // ── Notification redelivery backoff (I2) ─────────────────────────────

    [Fact]
    public void TryClaimOldestPendingNotification_FirstRetryIsImmediate()
    {
        _store.Create(CreateTask("sa-imm", "Transient failure", SubagentTaskState.Running));
        _store.TrySetTerminalResult("sa-imm", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));

        // First claim (attempts 0→1), then release → pending with a fresh notification_updated_at.
        Assert.NotNull(_store.TryClaimOldestPendingNotification());
        Assert.True(_store.ReleaseNotification("sa-imm"));

        // attempts == 1: a single transient failure must redeliver IMMEDIATELY (no backoff),
        // even though notification_updated_at was just stamped.
        var retried = _store.TryClaimOldestPendingNotification();
        Assert.NotNull(retried);
        Assert.Equal("sa-imm", retried!.TaskId);
        Assert.Equal(2, retried.NotificationAttempts);
    }

    [Fact]
    public void TryClaimOldestPendingNotification_ThrottlesPersistentFailureThenClaimsAfterDelay()
    {
        _store.Create(CreateTask("sa-backoff", "Persistent failure", SubagentTaskState.Running));
        _store.TrySetTerminalResult("sa-backoff", new SubagentExecutionResult(SubagentTaskState.Completed, "done"));

        // Simulate a notification that has already failed several times and was JUST released:
        // attempts high, notification_updated_at = now → still inside the exponential backoff window.
        SetNotification("sa-backoff", attempts: 4, updatedAt: DateTimeOffset.UtcNow);

        // Immediately-following pass: throttled — NOT re-enqueued (backoff observed).
        Assert.Null(_store.TryClaimOldestPendingNotification());

        // Once the backoff window has elapsed, the SAME notification IS claimable again.
        SetNotification("sa-backoff", attempts: 4, updatedAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10));
        var reclaimed = _store.TryClaimOldestPendingNotification();
        Assert.NotNull(reclaimed);
        Assert.Equal("sa-backoff", reclaimed!.TaskId);
        Assert.Equal(5, reclaimed.NotificationAttempts); // incremented on claim
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static SubagentTask CreateTask(
        string taskId,
        string description,
        SubagentTaskState state = SubagentTaskState.Running,
        string parentConversation = "conv-1",
        string parentChannel = "webchat-default",
        DateTimeOffset? createdAt = null)
    {
        return new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = parentConversation,
            ParentChannel = parentChannel,
            Description = description,
            Prompt = "Find all TODOs",
            State = state,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
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

    /// <summary>
    /// Directly set a pending notification's attempt count and last-attempt timestamp, so the
    /// deterministic exponential-backoff window can be exercised without a fake clock.
    /// </summary>
    private void SetNotification(string taskId, int attempts, DateTimeOffset updatedAt)
    {
        var dbPath = Path.Combine(_tempDir, "subagents", "subagents.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE subagent_tasks
            SET notification_state = 'pending',
                notification_attempts = $attempts,
                notification_updated_at = $updatedAt
            WHERE task_id = $taskId
            """;
        cmd.Parameters.AddWithValue("$taskId", taskId);
        cmd.Parameters.AddWithValue("$attempts", attempts);
        cmd.Parameters.AddWithValue("$updatedAt", updatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
