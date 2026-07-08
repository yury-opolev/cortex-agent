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
    private readonly ILogger<SubAgentStopTool> logger;

    public SubAgentStopTool(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        ILogger<SubAgentStopTool> logger)
    {
        this.store = store;
        this.registry = registry;
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
        string taskId;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            taskId = doc.RootElement.GetProperty("task_id").GetString() ?? string.Empty;
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

        switch (task.State)
        {
            case SubagentTaskState.Running or SubagentTaskState.Revising:
                if (this.registry.TryCancel(taskId))
                {
                    // The runner's own catch/finally transitions state to Cancelled,
                    // notifies the main agent, and dequeues the next task.
                    this.LogStopRequested(taskId);
                    return Task.FromResult(AgentToolResult.Ok(
                        $"Stopping subagent {taskId}. It will report as stopped shortly."));
                }

                // Store says running but no live runner (e.g. mid-transition) — mark stopped defensively.
                this.store.UpdateState(taskId, SubagentTaskState.Cancelled, result: "[Subagent stopped]");
                this.LogStopRequested(taskId);
                return Task.FromResult(AgentToolResult.Ok($"Subagent {taskId} marked stopped."));

            case SubagentTaskState.Queued:
                this.store.UpdateState(taskId, SubagentTaskState.Cancelled, result: "[Subagent stopped before starting]");
                this.LogQueuedCancelled(taskId);
                return Task.FromResult(AgentToolResult.Ok($"Queued subagent {taskId} cancelled."));

            default:
                var state = task.State.ToStorageValue();
                return Task.FromResult(AgentToolResult.Ok(
                    $"Subagent {taskId} is already {state}; nothing to stop."));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Stop requested: {TaskId}")]
    private partial void LogStopRequested(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_stop] Queued task cancelled: {TaskId}")]
    private partial void LogQueuedCancelled(string taskId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_stop] Task not found: {TaskId}")]
    private partial void LogNotFound(string taskId);
}
