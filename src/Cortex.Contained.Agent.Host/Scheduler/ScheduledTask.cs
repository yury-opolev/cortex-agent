namespace Cortex.Contained.Agent.Host.Scheduler;

/// <summary>
/// A task scheduled for future execution. Persisted in SQLite.
/// When a task fires, it runs in an isolated ephemeral session.
/// The instruction and response are persisted to the Bridge's
/// <c>scheduled-tasks</c> history channel. If the user wants
/// results delivered to a specific channel, they include that
/// instruction in the task message and the agent uses <c>send_message</c>.
/// </summary>
public sealed class ScheduledTask
{
    /// <summary>Unique task ID.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable description of what the task does.</summary>
    public required string Description { get; set; }

    /// <summary>
    /// The message text to send when the task fires.
    /// This is the prompt/instruction for what the agent should do.
    /// </summary>
    public required string MessageText { get; set; }

    /// <summary>When the task should first fire (UTC).</summary>
    public required DateTimeOffset ScheduledAtUtc { get; init; }

    /// <summary>
    /// Cron expression for recurrence (e.g. "30 9 * * *" for daily at 9:30 AM UTC).
    /// Standard 5-field format (minute hour day-of-month month day-of-week).
    /// Null means one-shot (fires once then completes).
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// Maximum number of times to execute this task. Null means unlimited.
    /// Only meaningful when <see cref="CronExpression"/> is set.
    /// When <see cref="ExecutionCount"/> reaches this value, the task is marked completed.
    /// </summary>
    public int? MaxExecutions { get; set; }

    /// <summary>When the task was created (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Current task status.</summary>
    public ScheduledTaskStatus Status { get; set; } = ScheduledTaskStatus.Pending;

    /// <summary>Last execution time (UTC), if any.</summary>
    public DateTimeOffset? LastExecutedAtUtc { get; set; }

    /// <summary>Next scheduled execution time (UTC). Updated for recurring tasks.</summary>
    public DateTimeOffset NextExecutionUtc { get; set; }

    /// <summary>Number of times the task has executed.</summary>
    public int ExecutionCount { get; set; }

    /// <summary>
    /// Target channel for response delivery (e.g. "webchat-default", "discord-dm").
    /// Included in the enriched message so the LLM knows which channel to use
    /// when calling send_message. If null, the LLM must determine the channel itself.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Whether this task uses cron-based recurrence.
    /// </summary>
    internal bool IsRecurring => !string.IsNullOrWhiteSpace(CronExpression);
}

/// <summary>
/// Lifecycle status of a scheduled task.
/// </summary>
public enum ScheduledTaskStatus
{
    /// <summary>Waiting for its next execution time.</summary>
    Pending,

    /// <summary>Currently being executed (enqueued and awaiting LLM processing).</summary>
    Running,

    /// <summary>Finished all executions (one-shot completed or max executions reached).</summary>
    Completed,

    /// <summary>Cancelled by the user.</summary>
    Cancelled,
}
