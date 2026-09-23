using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Stops a subagent task: cancels a running runner's loop, or drops a still-queued
/// task. Transitions the task to <see cref="SubagentTaskState.Cancelled"/>. Symmetric
/// with sub_agent_start / sub_agent_read / sub_agent_send.
/// </summary>
public sealed partial class SubAgentStopTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly SubagentExecutionCoordinator coordinator;
    private readonly ILogger<SubAgentStopTool> logger;

    public SubAgentStopTool(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        SubagentExecutionCoordinator coordinator,
        ILogger<SubAgentStopTool> logger)
    {
        this.store = store;
        this.registry = registry;
        this.coordinator = coordinator;
        this.logger = logger;
    }

    public string Name => "sub_agent_stop";

    public string Description =>
        "Stop a background subagent task you started. Cancels a running subagent's " +
        "work or drops a queued one. Use sub_agent_read first if you need its current " +
        "state or partial result. Provide the task_id returned by sub_agent_start.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID of the subagent to stop"
            }
          },
          "required": ["task_id"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string? taskId;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            taskId = doc.RootElement.TryGetProperty("task_id", out var taskIdElement)
                ? taskIdElement.GetString()
                : null;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: task_id"));
        }

        var task = this.store.GetById(taskId);
        if (task is null)
        {
            this.LogNotFound(taskId);
            return Task.FromResult(AgentToolResult.Fail($"No subagent task found with ID '{taskId}'."));
        }

        // Ownership: a caller may only stop tasks it started, or descendants of its own task.
        // Since this branch lets subagents use the sub_agent_* family, without this a
        // prompt-injected subagent could cancel any run in the store — including a depth-0 task
        // the user asked for, and its whole subtree.
        if (!this.CallerOwns(task, context))
        {
            return Task.FromResult(AgentToolResult.Fail(
                $"Subagent '{taskId}' was not started from this conversation; you can only stop tasks you own."));
        }

        // Already terminal: nothing to stop, and nothing to cascade — the subtree was cascaded
        // when this task reached its terminal state.
        if (task.State is SubagentTaskState.Completed or SubagentTaskState.Failed or SubagentTaskState.Cancelled)
        {
            return Task.FromResult(AgentToolResult.Ok(
                $"Subagent {taskId} is already {task.State.ToStorageValue()}; nothing to stop."));
        }

        // Cascade before stopping the parent. A stopped parent must not orphan children that keep
        // burning budget with nobody left to report to — and under a multi-day budget an orphan
        // can outlive its parent by days. Depth-first so leaves are cancelled before branches.
        var cascaded = this.StopSubtree(taskId);

        switch (task.State)
        {
            case SubagentTaskState.Running or SubagentTaskState.Revising:
                if (this.registry.TryCancel(taskId))
                {
                    // Cancelling the registry-owned token unwinds the loop; the coordinator's
                    // per-task-cancel path records the terminal Cancelled state exactly once.
                    this.LogStopRequested(taskId);
                    return Task.FromResult(AgentToolResult.Ok(
                        $"Stopping subagent {taskId}. It will report as stopped shortly." + FormatCascade(cascaded)));
                }

                // Store says running but no live runner (e.g. mid-transition) — record stopped
                // defensively through the guarded terminal write (never overwrites a real terminal).
                this.store.TrySetTerminalResult(
                    taskId, new SubagentExecutionResult(SubagentTaskState.Cancelled, "[Subagent stopped]"));
                // Wake the coordinator so the cancellation completion notification is delivered
                // promptly (this path did not go through the coordinator's slot-release signal).
                this.coordinator.SignalWorkAvailable();
                this.LogStopRequested(taskId);
                return Task.FromResult(AgentToolResult.Ok($"Subagent {taskId} marked stopped." + FormatCascade(cascaded)));

            case SubagentTaskState.Queued:
                // Conditional terminal transition that creates a pending notification. Guarded, so it
                // never writes Completed and never overwrites an already-terminal task.
                this.store.TrySetTerminalResult(
                    taskId,
                    new SubagentExecutionResult(SubagentTaskState.Cancelled, "[Subagent stopped before starting]"));
                // Close the narrow queued-cancel-vs-claim race: if the coordinator just registered a
                // runner for this task between our state read and the terminal write, cancel it so it
                // does not execute in a wasted slot. Harmless no-op when nothing is registered.
                this.registry.TryCancel(taskId);
                // Wake the coordinator so the cancellation completion notification is delivered promptly.
                this.coordinator.SignalWorkAvailable();
                this.LogQueuedCancelled(taskId);
                return Task.FromResult(AgentToolResult.Ok($"Queued subagent {taskId} cancelled." + FormatCascade(cascaded)));

            default:
                var state = task.State.ToStorageValue();
                return Task.FromResult(AgentToolResult.Ok(
                    $"Subagent {taskId} is already {state}; nothing to stop." + FormatCascade(cascaded)));
        }
    }

    /// <summary>
    /// Cancels every live descendant of <paramref name="rootTaskId"/>, deepest first, and returns
    /// how many were stopped. Depth-first matters: cancelling a branch before its leaves would
    /// leave the leaves briefly parentless and still consuming pool slots.
    /// </summary>
    /// <summary>
    /// A caller may only stop tasks it started, or descendants of its own task.
    /// <c>ToolExecutionContext.ConversationId</c> comes from the agent loop's own config, never
    /// from model-supplied arguments, so it is a trustworthy identity here.
    /// </summary>
    private bool CallerOwns(SubagentTask task, ToolExecutionContext context)
    {
        if (string.Equals(task.ParentConversation, context.ConversationId, StringComparison.Ordinal))
        {
            return true;
        }

        if (!SubagentConversationIds.TryGetTaskId(context.ConversationId, out var callerTaskId))
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = task;
        while (current?.ParentTaskId is { } parentId && seen.Add(parentId))
        {
            if (string.Equals(parentId, callerTaskId, StringComparison.Ordinal))
            {
                return true;
            }

            current = this.store.GetById(parentId);
        }

        return false;
    }

    private int StopSubtree(string rootTaskId)
        => this.StopSubtree(rootTaskId, new HashSet<string>(StringComparer.Ordinal));

    private int StopSubtree(string rootTaskId, HashSet<string> visited)
    {
        // Guard the recursion rather than trusting the stored parent links. Depth is capped at
        // creation, but a cycle introduced by data corruption or a future repoint would otherwise
        // recurse until the stack blew — taking the host down while cancelling a subagent.
        if (!visited.Add(rootTaskId))
        {
            return 0;
        }

        var stopped = 0;
        foreach (var child in this.store.ListChildren(rootTaskId))
        {
            stopped += this.StopSubtree(child.TaskId, visited);

            // Cancel a live runner if there is one, then record the terminal state through the
            // guarded write so a child that already finished on its own is never overwritten.
            this.registry.TryCancel(child.TaskId);
            this.store.TrySetTerminalResult(
                child.TaskId,
                new SubagentExecutionResult(SubagentTaskState.Cancelled, "[Subagent stopped: parent was stopped]"));
            this.LogCascadeStopped(child.TaskId, rootTaskId);
            stopped++;
        }

        return stopped;
    }

    private static string FormatCascade(int cascaded) => cascaded == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $" Also stopped {cascaded} delegated child task(s).");

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Cascade-stopped {TaskId} because ancestor {RootTaskId} was stopped")]
    private partial void LogCascadeStopped(string taskId, string rootTaskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Stop requested: {TaskId}")]
    private partial void LogStopRequested(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Queued task cancelled: {TaskId}")]
    private partial void LogQueuedCancelled(string taskId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_stop] Task not found: {TaskId}")]
    private partial void LogNotFound(string taskId);
}
