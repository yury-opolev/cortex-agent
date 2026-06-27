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
}

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
        _ => "queued",
    };

    public static SubagentTaskState Parse(string value) => value switch
    {
        "queued" => SubagentTaskState.Queued,
        "running" => SubagentTaskState.Running,
        "revising" => SubagentTaskState.Revising,
        "completed" => SubagentTaskState.Completed,
        "failed" => SubagentTaskState.Failed,
        _ => SubagentTaskState.Queued,
    };
}
