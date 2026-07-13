using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Sends a follow-up message to a running or completed subagent task.
/// For a running runner: injects the message directly into its loop (picked up next round).
/// For a terminal task: queues a durable resume (RunMode.Resume) and signals the coordinator, which
/// admits and executes it. There is no background execution in this tool.
/// </summary>
public sealed partial class SubAgentSendTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly SubagentExecutionCoordinator coordinator;
    private readonly ILogger<SubAgentSendTool> logger;

    public SubAgentSendTool(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        SubagentExecutionCoordinator coordinator,
        ILogger<SubAgentSendTool> logger)
    {
        this.store = store;
        this.registry = registry;
        this.coordinator = coordinator;
        this.logger = logger;
    }

    public string Name => "sub_agent_send";

    public string Description =>
        "Send a follow-up message to a subagent task. " +
        "If running: the message is queued and picked up on the next round. " +
        "If completed or failed: the subagent resumes with the new instruction.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID to send a message to"
            },
            "message": {
              "type": "string",
              "description": "The follow-up instruction or feedback"
            }
          },
          "required": ["task_id", "message"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string taskId;
        string message;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            taskId = root.GetProperty("task_id").GetString() ?? string.Empty;
            message = root.GetProperty("message").GetString() ?? string.Empty;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(message))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameters: task_id and message"));
        }

        var task = this.store.GetById(taskId);
        if (task is null)
        {
            return Task.FromResult(AgentToolResult.Fail($"No subagent task found with ID '{taskId}'."));
        }

        return Task.FromResult(task.State switch
        {
            SubagentTaskState.Running or SubagentTaskState.Revising => this.HandleRunningTask(task, message),
            SubagentTaskState.Completed or SubagentTaskState.Failed or SubagentTaskState.Cancelled =>
                this.HandleTerminalTask(task, message),
            SubagentTaskState.Queued => AgentToolResult.Fail(
                $"Task '{taskId}' is still queued and hasn't started yet. Wait for it to start before sending messages."),
            _ => AgentToolResult.Fail($"Task '{taskId}' is in unexpected state: {task.State.ToStorageValue()}"),
        });
    }

    private AgentToolResult HandleRunningTask(SubagentTask task, string message)
    {
        var runner = this.registry.TryGet(task.TaskId);
        if (runner is null)
        {
            // State says running but no live runner — likely a brief mid-transition race.
            this.LogRunnerNotFound(task.TaskId);
            return AgentToolResult.Fail(
                $"Task '{task.TaskId}' is marked as running but no active runner found. It may have just completed.");
        }

        runner.InjectMessage(message);
        this.LogMessageInjected(task.TaskId);

        return AgentToolResult.Ok(
            $"Message sent to running subagent '{task.TaskId}'. It will be processed on the next round.");
    }

    private AgentToolResult HandleTerminalTask(SubagentTask task, string message)
    {
        if (task.Messages.Count == 0)
        {
            return AgentToolResult.Fail(
                $"Task '{task.TaskId}' has no stored conversation history (purged after 24h). Cannot resume.");
        }

        // Durable, guarded resume: append the message, requeue as RunMode.Resume, clear terminal markers.
        if (!this.store.TryQueueResume(task.TaskId, message))
        {
            return AgentToolResult.Fail($"Could not queue a resume for '{task.TaskId}'.");
        }

        // Wake the coordinator so it claims and resumes the task when a slot is free.
        this.coordinator.SignalWorkAvailable();

        this.LogResumeQueued(task.TaskId);
        return AgentToolResult.Ok(
            $"Subagent '{task.TaskId}' resumed with your follow-up. It will deliver results when done.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_send] Message injected into running task {TaskId}")]
    private partial void LogMessageInjected(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_send] Task {TaskId} queued for resumption")]
    private partial void LogResumeQueued(string taskId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_send] Runner not found for task {TaskId}")]
    private partial void LogRunnerNotFound(string taskId);
}
