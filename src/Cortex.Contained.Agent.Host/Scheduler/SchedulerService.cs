using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.Agent;
using CronosExpression = Cronos.CronExpression;

namespace Cortex.Contained.Agent.Host.Scheduler;

/// <summary>
/// Lightweight in-process scheduler. Stores tasks in SQLite via <see cref="SqliteTaskStore"/>,
/// checks them on a timer, and enqueues deferred input messages into the agent's
/// processing queue when tasks are due. The agent processes these messages
/// through the LLM like any other user message.
/// </summary>
public sealed partial class SchedulerService : IDisposable
{
    private readonly AgentMessageChannel messageQueue;
    private readonly SqliteTaskStore store;
    private readonly ILogger<SchedulerService> logger;
    private readonly TimeProvider timeProvider;
    private readonly Timer timer;

    /// <summary>How often to check for due tasks.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often to run cleanup of old completed/cancelled tasks.</summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    private DateTimeOffset lastCleanup = DateTimeOffset.MinValue;

    /// <summary>Reentrancy guard: prevents overlapping tick executions.</summary>
    private int executing;

    public SchedulerService(
        AgentMessageChannel messageQueue,
        string dataPath,
        ILogger<SchedulerService> logger,
        TimeProvider? timeProvider = null)
    {
        this.messageQueue = messageQueue;
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;

        this.store = new SqliteTaskStore(dataPath);

        // Reset any tasks stuck in 'running' from a previous crash
        var reset = this.store.ResetRunningTasks();
        if (reset > 0)
        {
            this.LogTasksReset(reset);
        }

        var taskCount = this.store.GetAll().Count;

        // Start the tick timer
        this.timer = new Timer(OnTick, null, TickInterval, TickInterval);
        this.LogSchedulerStarted(taskCount, TickInterval.TotalSeconds);
    }

    /// <summary>Schedule a new task. Returns the task ID.</summary>
    public string Schedule(ScheduledTask task)
    {
        task.NextExecutionUtc = task.ScheduledAtUtc;
        this.store.Upsert(task);
        this.LogTaskScheduled(task.Id, task.Description, task.NextExecutionUtc);
        return task.Id;
    }

    /// <summary>Cancel a scheduled task. Returns true if found and cancelled.</summary>
    public bool Cancel(string taskId)
    {
        var cancelled = this.store.Cancel(taskId);
        if (cancelled)
        {
            this.LogTaskCancelled(taskId);
        }

        return cancelled;
    }

    /// <summary>Get all tasks (including completed/cancelled).</summary>
    public IReadOnlyList<ScheduledTask> GetAll()
    {
        return this.store.GetAll();
    }

    /// <summary>Get only active (pending/running) tasks.</summary>
    public IReadOnlyList<ScheduledTask> GetActive()
    {
        return this.store.GetActive();
    }

    /// <summary>Get a task by ID, or null if not found.</summary>
    public ScheduledTask? GetTask(string taskId)
    {
        return this.store.GetById(taskId);
    }

    /// <summary>Delete all tasks. Returns the number of tasks deleted.</summary>
    public int ClearAll()
    {
        return this.store.DeleteAll();
    }

    private void OnTick(object? state)
    {
        _ = ExecuteDueTasksAsync();
    }

    internal async Task ExecuteDueTasksAsync()
    {
        // Reentrancy guard: if a previous tick is still running, skip this one.
        // Timer callbacks can overlap because OnTick fires-and-forgets.
        if (Interlocked.CompareExchange(ref this.executing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = this.timeProvider.GetUtcNow();

            // Periodic cleanup of old completed/cancelled tasks
            if (now - this.lastCleanup > CleanupInterval)
            {
                this.lastCleanup = now;
                try
                {
                    var purged = this.store.Cleanup();
                    if (purged > 0)
                    {
                        this.LogTasksCleanedUp(purged);
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types -- cleanup must not crash the scheduler
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    this.LogCleanupFailed(ex.Message);
                }
            }

            var dueTasks = this.store.GetDue(now);

            if (dueTasks.Count == 0)
            {
                return;
            }

            foreach (var task in dueTasks)
            {
                var enqueued = false;

                // Mark as running and persist immediately, so a concurrent tick
                // or process restart won't pick it up again.
                task.Status = ScheduledTaskStatus.Running;
                this.store.Upsert(task);

                try
                {
                    this.LogTaskExecuting(task.Id, task.Description);

                    // Build enriched message text with task context
                    var enrichedText = BuildEnrichedMessageText(task);

                    // Enqueue the task's message into the agent's processing queue.
                    // All scheduled tasks serialize on the "scheduled-tasks" lane in
                    // the agent runtime, so concurrent execution never happens.
                    var agentMessage = new AgentMessage
                    {
                        ConversationId = $"scheduled-{task.Id}",
                        ChannelId = task.ChannelId ?? "scheduled",
                        Text = enrichedText,
                        Source = AgentMessageSource.ScheduledTask,
                        CorrelationId = Guid.NewGuid().ToString("N"),
                        Timestamp = now,
                    };

                    await this.messageQueue.EnqueueAsync(agentMessage).ConfigureAwait(false);
                    enqueued = true;

                    task.LastExecutedAtUtc = now;
                    task.ExecutionCount++;

                    this.LogTaskEnqueued(task.Id);
                }
#pragma warning disable CA1031 // Do not catch general exception types -- scheduler must not crash
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    this.LogTaskExecutionFailed(task.Id, ex.Message);
                }
                finally
                {
                    // Always advance and persist. If the enqueue failed, revert
                    // to pending so the task retries on the next tick.
                    if (enqueued)
                    {
                        try
                        {
                            if (task.MaxExecutions.HasValue && task.ExecutionCount >= task.MaxExecutions.Value)
                            {
                                task.Status = ScheduledTaskStatus.Completed;
                                this.LogTaskMaxExecutionsReached(task.Id, task.ExecutionCount);
                            }
                            else
                            {
                                // AdvanceNextExecution always sets the final status:
                                // Pending for recurring tasks with a future run,
                                // Completed for one-shots and exhausted cron expressions.
                                AdvanceNextExecution(task, now);
                            }
                        }
#pragma warning disable CA1031 // Invalid cron must not cause infinite re-firing
                        catch (Exception ex)
#pragma warning restore CA1031
                        {
                            task.Status = ScheduledTaskStatus.Completed;
                            this.LogAdvanceFailed(task.Id, ex.Message);
                        }
                    }
                    else
                    {
                        // Enqueue failed — revert to pending for retry
                        task.Status = ScheduledTaskStatus.Pending;
                    }

                    this.store.Upsert(task);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref this.executing, 0);
        }
    }

    /// <summary>
    /// Builds an enriched message that includes task context (ID, description,
    /// instructions, last execution time, execution count) so the LLM has
    /// full awareness of the task it is executing.
    /// </summary>
    internal static string BuildEnrichedMessageText(ScheduledTask task)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"[Scheduled Task: {task.Id}]");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Description: {task.Description}");

        if (task.LastExecutedAtUtc.HasValue)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Last run: {task.LastExecutedAtUtc.Value:yyyy-MM-dd HH:mm:ss UTC}");
        }

        if (task.ExecutionCount > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Execution count: {task.ExecutionCount}");
        }

        if (task.IsRecurring)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Schedule: {task.CronExpression}");
        }

        if (task.ChannelId is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Target channel: {task.ChannelId}");
        }

        sb.AppendLine();
        sb.AppendLine("Instructions:");
        sb.Append(task.MessageText);

        return sb.ToString();
    }

    /// <summary>
    /// Advances <see cref="ScheduledTask.NextExecutionUtc"/> using the cron expression,
    /// or marks the task completed if it's a one-shot (no cron).
    /// Always sets <see cref="ScheduledTask.Status"/>: <see cref="ScheduledTaskStatus.Pending"/>
    /// if the task has a future run, <see cref="ScheduledTaskStatus.Completed"/> otherwise.
    /// </summary>
    internal static void AdvanceNextExecution(ScheduledTask task, DateTimeOffset now)
    {
        if (!task.IsRecurring)
        {
            task.Status = ScheduledTaskStatus.Completed;
            return;
        }

        var cron = CronosExpression.Parse(task.CronExpression!);
        var next = cron.GetNextOccurrence(now.UtcDateTime, inclusive: false);

        if (next.HasValue)
        {
            task.NextExecutionUtc = new DateTimeOffset(next.Value, TimeSpan.Zero);
            task.Status = ScheduledTaskStatus.Pending;
        }
        else
        {
            // Cron expression has no future occurrences (shouldn't happen with standard expressions)
            task.Status = ScheduledTaskStatus.Completed;
        }
    }

    public void Dispose()
    {
        this.timer.Dispose();
        this.store.Dispose();
    }

    // ── LoggerMessage source-generated methods ───────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduler started with {TaskCount} persisted tasks, tick interval={TickIntervalSeconds}s")]
    private partial void LogSchedulerStarted(int taskCount, double tickIntervalSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task scheduled: {TaskId} - {Description}, next execution={NextExecution}")]
    private partial void LogTaskScheduled(string taskId, string description, DateTimeOffset nextExecution);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task cancelled: {TaskId}")]
    private partial void LogTaskCancelled(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing task {TaskId}: {Description}")]
    private partial void LogTaskExecuting(string taskId, string description);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId} enqueued for LLM processing")]
    private partial void LogTaskEnqueued(string taskId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {TaskId} execution failed: {ErrorMessage}")]
    private partial void LogTaskExecutionFailed(string taskId, string? errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleaned up {Count} old completed/cancelled tasks")]
    private partial void LogTasksCleanedUp(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Task cleanup failed: {ErrorMessage}")]
    private partial void LogCleanupFailed(string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Task {TaskId} reached max executions ({ExecutionCount}), marking completed")]
    private partial void LogTaskMaxExecutionsReached(string taskId, int executionCount);

    [LoggerMessage(Level = LogLevel.Error, Message = "Task {TaskId} has invalid cron expression, marking completed: {ErrorMessage}")]
    private partial void LogAdvanceFailed(string taskId, string? errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reset {Count} tasks from running to pending after restart")]
    private partial void LogTasksReset(int count);
}
