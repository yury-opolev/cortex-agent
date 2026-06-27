using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Scheduler;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Agent.Host.Tests;

public class SchedulerServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AgentMessageChannel _messageChannel;
    private readonly FakeTimeProvider _timeProvider;
    private readonly SchedulerService _scheduler;

    public SchedulerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "james-scheduler-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _messageChannel = new AgentMessageChannel();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero));
        _scheduler = new SchedulerService(
            _messageChannel,
            _tempDir,
            NullLogger<SchedulerService>.Instance,
            _timeProvider);
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Basic scheduling ─────────────────────────────────────────────────

    [Fact]
    public void Schedule_AddsTask()
    {
        var task = CreateOneShotTask("test-1", "Test task", minutesFromNow: 10);

        _scheduler.Schedule(task);

        var all = _scheduler.GetAll();
        Assert.Single(all);
        Assert.Equal("test-1", all[0].Id);
    }

    [Fact]
    public void GetActive_ExcludesCompletedAndCancelled()
    {
        var active = CreateOneShotTask("t-1", "Active", minutesFromNow: 10);
        var completed = CreateOneShotTask("t-2", "Completed", minutesFromNow: 10);
        completed.Status = ScheduledTaskStatus.Completed;
        var cancelled = CreateOneShotTask("t-3", "Cancelled", minutesFromNow: 10);
        cancelled.Status = ScheduledTaskStatus.Cancelled;

        _scheduler.Schedule(active);
        _scheduler.Schedule(completed);
        _scheduler.Schedule(cancelled);

        var activeTasks = _scheduler.GetActive();
        Assert.Single(activeTasks);
        Assert.Equal("t-1", activeTasks[0].Id);
    }

    [Fact]
    public void Cancel_SetsTaskCancelled()
    {
        var task = CreateOneShotTask("t-1", "Will cancel", minutesFromNow: 10);
        _scheduler.Schedule(task);

        var result = _scheduler.Cancel("t-1");

        Assert.True(result);
        var retrieved = _scheduler.GetTask("t-1");
        Assert.NotNull(retrieved);
        Assert.Equal(ScheduledTaskStatus.Cancelled, retrieved!.Status);
    }

    [Fact]
    public void Cancel_NonExistent_ReturnsFalse()
    {
        Assert.False(_scheduler.Cancel("does-not-exist"));
    }

    [Fact]
    public void GetTask_ExistingId_ReturnsTask()
    {
        var task = CreateOneShotTask("t-1", "Find me", minutesFromNow: 5);
        _scheduler.Schedule(task);

        var found = _scheduler.GetTask("t-1");
        Assert.NotNull(found);
        Assert.Equal("Find me", found!.Description);
    }

    [Fact]
    public void GetTask_NonExistent_ReturnsNull()
    {
        Assert.Null(_scheduler.GetTask("nope"));
    }

    [Fact]
    public void GetAll_OrderedByNextExecution()
    {
        var task1 = CreateOneShotTask("t-later", "Later", minutesFromNow: 60);
        var task2 = CreateOneShotTask("t-sooner", "Sooner", minutesFromNow: 10);
        var task3 = CreateOneShotTask("t-soonest", "Soonest", minutesFromNow: 1);

        _scheduler.Schedule(task1);
        _scheduler.Schedule(task2);
        _scheduler.Schedule(task3);

        var all = _scheduler.GetAll();
        Assert.Equal(3, all.Count);
        Assert.Equal("t-soonest", all[0].Id);
        Assert.Equal("t-sooner", all[1].Id);
        Assert.Equal("t-later", all[2].Id);
    }

    [Fact]
    public void Schedule_PersistsToDisk()
    {
        var task = CreateOneShotTask("t-persist", "Persisted task", minutesFromNow: 10);
        _scheduler.Schedule(task);

        // Create a new scheduler from the same path to verify persistence
        using var scheduler2 = new SchedulerService(
            _messageChannel, _tempDir, NullLogger<SchedulerService>.Instance);

        var loaded = scheduler2.GetTask("t-persist");
        Assert.NotNull(loaded);
        Assert.Equal("Persisted task", loaded!.Description);
    }

    // ── One-shot task execution ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteDueTasksAsync_DueOneShot_EnqueuesAndCompletes()
    {
        var task = CreateOneShotTask("t-1", "Due now", minutesFromNow: -1);
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var enqueued = await ReadOneMessage(_messageChannel, cts.Token);

        Assert.NotNull(enqueued);
        Assert.Equal("scheduled-t-1", enqueued!.ConversationId);
        Assert.Equal("scheduled", enqueued.ChannelId);
        Assert.Equal(AgentMessageSource.ScheduledTask, enqueued.Source);

        var completed = _scheduler.GetTask("t-1");
        Assert.NotNull(completed);
        Assert.Equal(ScheduledTaskStatus.Completed, completed!.Status);
        Assert.Equal(1, completed.ExecutionCount);
    }

    [Fact]
    public async Task ExecuteDueTasksAsync_TaskWithChannelId_UsesChannelIdOnMessage()
    {
        var task = new ScheduledTask
        {
            Id = "t-ch",
            Description = "Channel-targeted",
            MessageText = "Hello Discord!",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            ChannelId = "discord-dm",
        };
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var enqueued = await ReadOneMessage(_messageChannel, cts.Token);

        Assert.NotNull(enqueued);
        Assert.Equal("discord-dm", enqueued!.ChannelId);
        Assert.Equal("scheduled-t-ch", enqueued.ConversationId);
    }

    [Fact]
    public async Task ExecuteDueTasksAsync_FutureTask_NotEnqueued()
    {
        var task = CreateOneShotTask("t-1", "Future", minutesFromNow: 60);
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();

        var hasMessage = _messageChannel.TryRead(out _);
        Assert.False(hasMessage);
        Assert.Equal(ScheduledTaskStatus.Pending, _scheduler.GetTask("t-1")!.Status);
    }

    // ── Cron-based recurring tasks ───────────────────────────────────────

    [Fact]
    public async Task ExecuteDueTasksAsync_CronTask_ReschedulesToNextOccurrence()
    {
        var task = new ScheduledTask
        {
            Id = "t-hourly",
            Description = "Hourly check",
            MessageText = "Ping!",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            CronExpression = "0 * * * *", // Every hour on the hour
        };
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        var retrieved = _scheduler.GetTask("t-hourly");
        Assert.NotNull(retrieved);
        Assert.Equal(ScheduledTaskStatus.Pending, retrieved!.Status);
        Assert.Equal(1, retrieved.ExecutionCount);

        // Next execution should be at 13:00 (the next hour boundary after 12:00)
        var expectedNext = new DateTimeOffset(2026, 3, 11, 13, 0, 0, TimeSpan.Zero);
        Assert.Equal(expectedNext, retrieved.NextExecutionUtc);
    }

    [Fact]
    public async Task ExecuteDueTasksAsync_CronTask_MultipleRuns_AdvancesCorrectly()
    {
        var task = new ScheduledTask
        {
            Id = "t-30min",
            Description = "Half-hourly",
            MessageText = "Check!",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            CronExpression = "*/30 * * * *", // :00 and :30
        };
        _scheduler.Schedule(task);

        // First tick at 12:00
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        var after1 = _scheduler.GetTask("t-30min")!;
        Assert.Equal(1, after1.ExecutionCount);
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 12, 30, 0, TimeSpan.Zero), after1.NextExecutionUtc);

        // Advance to 12:30
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 11, 12, 30, 1, TimeSpan.Zero));
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        var after2 = _scheduler.GetTask("t-30min")!;
        Assert.Equal(2, after2.ExecutionCount);
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 13, 0, 0, TimeSpan.Zero), after2.NextExecutionUtc);
    }

    [Fact]
    public async Task ExecuteDueTasksAsync_CronTask_ContainerDowntime_SkipsMissedRuns()
    {
        var task = new ScheduledTask
        {
            Id = "t-daily",
            Description = "Daily report",
            MessageText = "Generate report",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            CronExpression = "0 0 * * *", // Daily at midnight
        };
        _scheduler.Schedule(task);

        // First run at 12:00 on March 11
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        // Simulate container downtime: jump 3 days ahead to March 14, 15:00
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 14, 15, 0, 0, TimeSpan.Zero));
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        var after = _scheduler.GetTask("t-daily")!;
        Assert.Equal(2, after.ExecutionCount); // Only ran twice, not 4 times
        // Next execution should be March 15 at midnight
        Assert.Equal(new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero), after.NextExecutionUtc);
    }

    // ── Max executions ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteDueTasksAsync_MaxExecutions_CompletesAfterLimit()
    {
        var task = new ScheduledTask
        {
            Id = "t-limited",
            Description = "Run 3 times",
            MessageText = "Work!",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            CronExpression = "*/15 * * * *", // Every 15 min
            MaxExecutions = 3,
        };
        _scheduler.Schedule(task);

        // Run 1
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);
        Assert.Equal(1, _scheduler.GetTask("t-limited")!.ExecutionCount);
        Assert.Equal(ScheduledTaskStatus.Pending, _scheduler.GetTask("t-limited")!.Status);

        // Run 2
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 11, 12, 15, 1, TimeSpan.Zero));
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);
        Assert.Equal(2, _scheduler.GetTask("t-limited")!.ExecutionCount);
        Assert.Equal(ScheduledTaskStatus.Pending, _scheduler.GetTask("t-limited")!.Status);

        // Run 3 — should hit max and complete
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 11, 12, 30, 1, TimeSpan.Zero));
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);
        Assert.Equal(3, _scheduler.GetTask("t-limited")!.ExecutionCount);
        Assert.Equal(ScheduledTaskStatus.Completed, _scheduler.GetTask("t-limited")!.Status);
    }

    [Fact]
    public async Task ExecuteDueTasksAsync_MaxExecutions_DoesNotRunAfterCompleted()
    {
        var task = new ScheduledTask
        {
            Id = "t-once-cron",
            Description = "Run once with cron",
            MessageText = "One time!",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            CronExpression = "*/5 * * * *",
            MaxExecutions = 1,
        };
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);
        Assert.Equal(ScheduledTaskStatus.Completed, _scheduler.GetTask("t-once-cron")!.Status);

        // Advance time — task should not fire again
        _timeProvider.SetUtcNow(new DateTimeOffset(2026, 3, 11, 12, 5, 1, TimeSpan.Zero));
        await _scheduler.ExecuteDueTasksAsync();
        var hasMessage = _messageChannel.TryRead(out _);
        Assert.False(hasMessage);
        Assert.Equal(1, _scheduler.GetTask("t-once-cron")!.ExecutionCount);
    }

    // ── Task status transitions ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteDueTasksAsync_TaskIsRunningDuringExecution()
    {
        // Verify that a due task transitions to Running status and is persisted
        // before the message is enqueued, preventing duplicate pickup.
        var task = CreateOneShotTask("t-running", "Check running state", minutesFromNow: -1);
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        // After execution completes, one-shot should be completed
        var after = _scheduler.GetTask("t-running")!;
        Assert.Equal(ScheduledTaskStatus.Completed, after.Status);
        Assert.Equal(1, after.ExecutionCount);
    }

    [Fact]
    public void RunningTasks_ResetToPending_OnRestart()
    {
        // Simulate a task stuck in running state (from a crash)
        var task = CreateOneShotTask("t-stuck", "Stuck task", minutesFromNow: -1);
        task.Status = ScheduledTaskStatus.Running;
        _scheduler.Schedule(task);

        // Create a new scheduler — it should reset running tasks to pending
        using var scheduler2 = new SchedulerService(
            _messageChannel, _tempDir, NullLogger<SchedulerService>.Instance, _timeProvider);

        var recovered = scheduler2.GetTask("t-stuck");
        Assert.NotNull(recovered);
        Assert.Equal(ScheduledTaskStatus.Pending, recovered!.Status);
    }

    // ── Enriched message text ────────────────────────────────────────────

    [Fact]
    public void BuildEnrichedMessageText_OneShotTask_ContainsIdAndInstructions()
    {
        var task = new ScheduledTask
        {
            Id = "t-enrich",
            Description = "Check weather",
            MessageText = "What is the weather in NYC?",
            ScheduledAtUtc = _timeProvider.GetUtcNow(),
        };

        var text = SchedulerService.BuildEnrichedMessageText(task);

        Assert.Contains("[Scheduled Task: t-enrich]", text);
        Assert.Contains("Description: Check weather", text);
        Assert.Contains("Instructions:", text);
        Assert.Contains("What is the weather in NYC?", text);
        Assert.DoesNotContain("Last run:", text);
        Assert.DoesNotContain("Execution count:", text);
        Assert.DoesNotContain("Schedule:", text);
    }

    [Fact]
    public void BuildEnrichedMessageText_RecurringTask_ContainsCronAndRunHistory()
    {
        var task = new ScheduledTask
        {
            Id = "t-cron",
            Description = "Hourly ping",
            MessageText = "Ping all services",
            ScheduledAtUtc = _timeProvider.GetUtcNow(),
            CronExpression = "0 * * * *",
            ExecutionCount = 5,
            LastExecutedAtUtc = new DateTimeOffset(2026, 3, 11, 11, 0, 0, TimeSpan.Zero),
        };

        var text = SchedulerService.BuildEnrichedMessageText(task);

        Assert.Contains("Schedule: 0 * * * *", text);
        Assert.Contains("Execution count: 5", text);
        Assert.Contains("Last run: 2026-03-11 11:00:00 UTC", text);
    }

    [Fact]
    public async Task ExecuteDueTasksAsync_EnrichedText_AppearsInEnqueuedMessage()
    {
        var task = CreateOneShotTask("t-msg", "Test enrichment", minutesFromNow: -1);
        _scheduler.Schedule(task);

        await _scheduler.ExecuteDueTasksAsync();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var enqueued = await ReadOneMessage(_messageChannel, cts.Token);

        Assert.NotNull(enqueued);
        Assert.Contains("[Scheduled Task: t-msg]", enqueued!.Text);
        Assert.Contains("Description: Test enrichment", enqueued.Text);
        Assert.Contains("Instructions:", enqueued.Text);
        Assert.Contains("Reminder: Test enrichment", enqueued.Text);
    }

    // ── Invalid cron expression ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteDueTasksAsync_InvalidCronExpression_FiresOnceThenCompletes()
    {
        // This reproduces the bug where a task with an invalid cron expression
        // would fire every tick because AdvanceNextExecution threw an exception
        // before the task state was persisted.
        var task = new ScheduledTask
        {
            Id = "t-bad-cron",
            Description = "Bad cron task",
            MessageText = "Should only fire once",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(-1),
            CronExpression = "0 */0 * * *", // Invalid: 0 is not a valid step for hours
        };
        _scheduler.Schedule(task);

        // First tick — task fires and gets enqueued, but cron advance fails.
        // Task should be marked completed so it doesn't re-fire.
        await _scheduler.ExecuteDueTasksAsync();
        await DrainMessages(_messageChannel);

        var afterFirst = _scheduler.GetTask("t-bad-cron")!;
        Assert.Equal(1, afterFirst.ExecutionCount);
        Assert.Equal(ScheduledTaskStatus.Completed, afterFirst.Status);

        // Second tick — task should NOT fire again
        await _scheduler.ExecuteDueTasksAsync();
        var hasMessage = _messageChannel.TryRead(out _);
        Assert.False(hasMessage, "Task with invalid cron should not fire a second time");
        Assert.Equal(1, _scheduler.GetTask("t-bad-cron")!.ExecutionCount);
    }

    // ── AdvanceNextExecution ──────────────────────────────────────────────

    [Fact]
    public void AdvanceNextExecution_OneShotTask_MarksCompleted()
    {
        var task = CreateOneShotTask("t-advance", "One-shot", minutesFromNow: 0);

        SchedulerService.AdvanceNextExecution(task, _timeProvider.GetUtcNow());

        Assert.Equal(ScheduledTaskStatus.Completed, task.Status);
    }

    [Fact]
    public void AdvanceNextExecution_CronTask_SetsNextOccurrence()
    {
        var now = new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);
        var task = new ScheduledTask
        {
            Id = "t-advance-cron",
            Description = "Test",
            MessageText = "Test",
            ScheduledAtUtc = now,
            CronExpression = "30 * * * *", // Every hour at :30
        };

        SchedulerService.AdvanceNextExecution(task, now);

        Assert.Equal(ScheduledTaskStatus.Pending, task.Status);
        Assert.Equal(new DateTimeOffset(2026, 3, 11, 12, 30, 0, TimeSpan.Zero), task.NextExecutionUtc);
    }

    // ── Schema ───────────────────────────────────────────────────────────

    [Fact]
    public void NewDatabase_PersistsNewFields()
    {
        var task = new ScheduledTask
        {
            Id = "t-schema",
            Description = "Schema test",
            MessageText = "Hello",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(10),
            CronExpression = "*/15 * * * *",
            MaxExecutions = 10,
        };
        _scheduler.Schedule(task);

        var loaded = _scheduler.GetTask("t-schema");
        Assert.NotNull(loaded);
        Assert.Equal("*/15 * * * *", loaded!.CronExpression);
        Assert.Equal(10, loaded.MaxExecutions);
        Assert.Equal(ScheduledTaskStatus.Pending, loaded.Status);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ScheduledTask CreateOneShotTask(string id, string description, int minutesFromNow)
    {
        return new ScheduledTask
        {
            Id = id,
            Description = description,
            MessageText = $"Reminder: {description}",
            ScheduledAtUtc = _timeProvider.GetUtcNow().AddMinutes(minutesFromNow),
        };
    }

    private static async Task<AgentMessage?> ReadOneMessage(AgentMessageChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var msg in channel.ReadAllAsync(cancellationToken))
            {
                return msg;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout
        }

        return null;
    }

    private static async Task DrainMessages(AgentMessageChannel channel)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await foreach (var _ in channel.ReadAllAsync(cts.Token))
            {
                // Drain
            }
        }
        catch (OperationCanceledException)
        {
            // Done
        }
    }
}
