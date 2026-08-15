namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Builds the synthetic parent-turn instruction announcing that a subagent task reached a
/// terminal state. Pure formatting, deliberately split out of
/// <see cref="SubagentExecutionCoordinator"/> so the wording is unit-testable without standing
/// up a store, a message channel and a dispatch loop.
/// <para>
/// The envelope is <b>state-aware</b>. It used to be state-blind, which caused a real incident
/// (2026-08-15): a task killed mid-stream by the LLM inactivity watchdog was recorded as
/// <see cref="SubagentTaskState.Failed"/> in the store but announced to the parent as
/// "[Background task completed]", with the error text pasted in where the result belongs. The
/// orchestrator could not tell success from failure, so it reported a lost 25-minute research
/// run to the user as a finished task instead of resuming it.
/// </para>
/// </summary>
internal static class SubagentCompletionNotice
{
    /// <summary>Shown when the runner recorded no terminal text at all.</summary>
    private const string NoResult = "[no result recorded]";

    /// <summary>
    /// Formats the notification for <paramref name="task"/> from its DURABLE terminal record
    /// (whichever terminal write won).
    /// </summary>
    internal static string Build(SubagentTask task)
    {
        var body = string.IsNullOrWhiteSpace(task.Result) ? NoResult : task.Result;

        return task.State switch
        {
            SubagentTaskState.Completed => Compose(
                task,
                header: "[Background task completed]",
                bodyLabel: "Result",
                body: body,
                guidance:
                    "Review the result and respond to the user. "
                    + "If there is a follow-up task to do, use sub_agent_start. "
                    + $"Use sub_agent_read('{task.TaskId}') if you need more details."),

            SubagentTaskState.Failed => Compose(
                task,
                header: "[Background task FAILED]",
                bodyLabel: "Failure",
                body: body,
                guidance:
                    "The task did NOT finish. Its work up to the point of failure is preserved "
                    + $"(history is checkpointed every round), so use sub_agent_send('{task.TaskId}', "
                    + "'<continue instruction>') to RESUME it rather than starting it over. "
                    + $"Use sub_agent_read('{task.TaskId}') to inspect how far it got. "
                    + "Tell the user the task failed and what you are doing about it."),

            SubagentTaskState.Cancelled => Compose(
                task,
                header: "[Background task cancelled]",
                bodyLabel: "Outcome",
                body: body,
                guidance:
                    "The task was stopped before finishing. Its work up to that point is preserved "
                    + $"— use sub_agent_send('{task.TaskId}', '<continue instruction>') to resume it "
                    + "if the user wants it continued."),

            // Not expected: only terminal tasks are notified. Never dress an unfinished task up
            // as a success — report the state honestly and let the parent decide.
            _ => Compose(
                task,
                header: "[Background task finished in an unexpected state]",
                bodyLabel: "Outcome",
                body: body,
                guidance:
                    $"The task is recorded as '{task.State}', which is not a terminal state. "
                    + $"Use sub_agent_read('{task.TaskId}') to inspect it before reporting to the user."),
        };
    }

    private static string Compose(
        SubagentTask task, string header, string bodyLabel, string body, string guidance)
        => $"{header}\n"
            + $"Task: \"{task.Description}\" ({task.TaskId})\n\n"
            + $"{bodyLabel}:\n{body}\n\n"
            + guidance;
}
