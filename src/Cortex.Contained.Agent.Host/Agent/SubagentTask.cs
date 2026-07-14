using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Lifecycle state of a subagent task.
/// </summary>
public enum SubagentTaskState
{
    /// <summary>Waiting for a concurrency slot to become available.</summary>
    Queued,

    /// <summary>Subagent runner is actively executing.</summary>
    Running,

    /// <summary>Subagent finished; eval decided to resume with steering instructions.</summary>
    Revising,

    /// <summary>Subagent completed and result was delivered to the user.</summary>
    Completed,

    /// <summary>Subagent failed (LLM error, max rounds, crash).</summary>
    Failed,

    /// <summary>Subagent was stopped via sub_agent_stop (running loop cancelled or queued task dropped).</summary>
    Cancelled,
}

/// <summary>
/// How the subagent runner should start executing a task.
/// </summary>
public enum SubagentRunMode
{
    /// <summary>Start a fresh run from the prompt.</summary>
    New,

    /// <summary>Resume from the persisted message history.</summary>
    Resume,
}

/// <summary>
/// Delivery state of a task's terminal result notification to the parent conversation.
/// </summary>
public enum SubagentNotificationState
{
    /// <summary>No notification owed (task not terminal yet, or superseded by a resume).</summary>
    None,

    /// <summary>Terminal result recorded; delivery not yet attempted or released for retry.</summary>
    Pending,

    /// <summary>Claimed by a delivery worker; in-flight.</summary>
    Enqueued,

    /// <summary>Result was delivered to the parent conversation.</summary>
    Delivered,
}

/// <summary>
/// Terminal outcome of a subagent execution, applied via
/// <see cref="SubagentSessionStore.TrySetTerminalResult"/>.
/// </summary>
public sealed record SubagentExecutionResult(
    SubagentTaskState TerminalState,
    string Result);

/// <summary>
/// A background subagent task. Persisted in SQLite for durability across restarts.
/// </summary>
public sealed class SubagentTask
{
    /// <summary>Unique task identifier (e.g. "sa-7f3a...").</summary>
    public required string TaskId { get; init; }

    /// <summary>Conversation ID of the parent that spawned this task.</summary>
    public required string ParentConversation { get; init; }

    /// <summary>Channel ID for delivering results (e.g. "webchat-default").</summary>
    public required string ParentChannel { get; init; }

    /// <summary>Short human-readable description (3-5 words).</summary>
    public required string Description { get; init; }

    /// <summary>Full prompt given to the subagent.</summary>
    public required string Prompt { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public SubagentTaskState State { get; set; }

    /// <summary>
    /// Full LLM message history for the subagent session.
    /// Enables resumption via <c>sub_agent_send</c>.
    /// Purged after 24 hours to save space.
    /// </summary>
    public IReadOnlyList<LlmMessage> Messages { get; set; } = [];

    /// <summary>Final text output from the subagent runner.</summary>
    public string? Result { get; set; }

    /// <summary>Response composed by the eval LLM call and delivered to the user.</summary>
    public string? EvalResponse { get; set; }

    /// <summary>When the task was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the task reached a terminal state (completed/failed).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Number of LLM rounds executed by the subagent runner.</summary>
    public int Rounds { get; set; }

    /// <summary>Whether the next execution starts fresh or resumes from persisted history.</summary>
    public SubagentRunMode RunMode { get; set; } = SubagentRunMode.New;

    /// <summary>Optional skill the subagent was launched with.</summary>
    public string? SkillName { get; init; }

    /// <summary>Delivery state of the terminal result notification.</summary>
    public SubagentNotificationState NotificationState { get; set; }

    /// <summary>Number of times delivery of the terminal result has been attempted.</summary>
    public int NotificationAttempts { get; set; }

    /// <summary>When the notification state last changed.</summary>
    public DateTimeOffset? NotificationUpdatedAt { get; set; }

    /// <summary>When execution first started (survives restarts).</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Last time the task made observable progress.</summary>
    public DateTimeOffset LastProgressAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Number of times execution was restarted (crash recovery or requeue).</summary>
    public int RestartCount { get; set; }
}

/// <summary>
/// Extension methods for <see cref="SubagentTaskState"/> serialization to/from SQLite.
/// </summary>
public static class SubagentTaskStateExtensions
{
    public static string ToStorageValue(this SubagentTaskState state) => state switch
    {
        SubagentTaskState.Queued => "queued",
        SubagentTaskState.Running => "running",
        SubagentTaskState.Revising => "revising",
        SubagentTaskState.Completed => "completed",
        SubagentTaskState.Failed => "failed",
        SubagentTaskState.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown subagent task state."),
    };

    public static SubagentTaskState Parse(string value) => value switch
    {
        "queued" => SubagentTaskState.Queued,
        "running" => SubagentTaskState.Running,
        "revising" => SubagentTaskState.Revising,
        "completed" => SubagentTaskState.Completed,
        "failed" => SubagentTaskState.Failed,
        "cancelled" => SubagentTaskState.Cancelled,
        _ => throw new InvalidDataException($"Unknown persisted subagent task state '{value}'."),
    };
}

/// <summary>
/// Extension methods for <see cref="SubagentRunMode"/> serialization to/from SQLite.
/// </summary>
public static class SubagentRunModeExtensions
{
    public static string ToStorageValue(this SubagentRunMode runMode) => runMode switch
    {
        SubagentRunMode.New => "new",
        SubagentRunMode.Resume => "resume",
        _ => throw new ArgumentOutOfRangeException(nameof(runMode), runMode, "Unknown subagent run mode."),
    };

    public static SubagentRunMode Parse(string value) => value switch
    {
        "new" => SubagentRunMode.New,
        "resume" => SubagentRunMode.Resume,
        _ => throw new InvalidDataException($"Unknown persisted subagent run mode '{value}'."),
    };
}

/// <summary>
/// Extension methods for <see cref="SubagentNotificationState"/> serialization to/from SQLite.
/// </summary>
public static class SubagentNotificationStateExtensions
{
    public static string ToStorageValue(this SubagentNotificationState state) => state switch
    {
        SubagentNotificationState.None => "none",
        SubagentNotificationState.Pending => "pending",
        SubagentNotificationState.Enqueued => "enqueued",
        SubagentNotificationState.Delivered => "delivered",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown subagent notification state."),
    };

    public static SubagentNotificationState Parse(string value) => value switch
    {
        "none" => SubagentNotificationState.None,
        "pending" => SubagentNotificationState.Pending,
        "enqueued" => SubagentNotificationState.Enqueued,
        "delivered" => SubagentNotificationState.Delivered,
        _ => throw new InvalidDataException($"Unknown persisted subagent notification state '{value}'."),
    };
}
