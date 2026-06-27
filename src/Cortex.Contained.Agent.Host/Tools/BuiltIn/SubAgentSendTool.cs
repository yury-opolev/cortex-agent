using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Sends a follow-up message to a running or completed subagent task.
/// For running tasks: injects the message into the subagent's loop.
/// For completed/failed tasks: resumes the subagent with the new instruction.
/// </summary>
public sealed partial class SubAgentSendTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly ILlmClient llmClient;
    private readonly Func<ToolRegistry> toolRegistryFactory;
    private readonly IModelProvider modelProvider;
    private readonly IOptionsMonitor<AgentConfig> agentConfig;
    private readonly Func<string, string, Task> onCompletion;
    private readonly InMemoryTodoStore? todoStore;
    private readonly ILogger<SubAgentSendTool> logger;
    private readonly IOptionsMonitor<ImageAgingConfig>? imageAgingOptions;
    private readonly IImageDescriber? imageDescriber;

    public SubAgentSendTool(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        ILlmClient llmClient,
        Func<ToolRegistry> toolRegistryFactory,
        IModelProvider modelProvider,
        IOptionsMonitor<AgentConfig> agentConfig,
        Func<string, string, Task> onCompletion,
        ILogger<SubAgentSendTool> logger,
        InMemoryTodoStore? todoStore = null,
        IOptionsMonitor<ImageAgingConfig>? imageAgingOptions = null,
        IImageDescriber? imageDescriber = null)
    {
        this.store = store;
        this.registry = registry;
        this.llmClient = llmClient;
        this.toolRegistryFactory = toolRegistryFactory;
        this.modelProvider = modelProvider;
        this.agentConfig = agentConfig;
        this.onCompletion = onCompletion;
        this.todoStore = todoStore;
        this.logger = logger;
        this.imageAgingOptions = imageAgingOptions;
        this.imageDescriber = imageDescriber;
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

        return task.State switch
        {
            SubagentTaskState.Running or SubagentTaskState.Revising => HandleRunningTask(task, message),
            SubagentTaskState.Completed or SubagentTaskState.Failed => HandleCompletedTask(task, message, cancellationToken),
            SubagentTaskState.Queued => Task.FromResult(AgentToolResult.Fail($"Task '{taskId}' is still queued and hasn't started yet. Wait for it to start before sending messages.")),
            _ => Task.FromResult(AgentToolResult.Fail($"Task '{taskId}' is in unexpected state: {task.State.ToStorageValue()}")),
        };
    }

    private Task<AgentToolResult> HandleRunningTask(SubagentTask task, string message)
    {
        var runner = this.registry.TryGet(task.TaskId);
        if (runner is null)
        {
            // Runner not in memory but state says running — likely a race condition
            this.LogRunnerNotFound(task.TaskId);
            return Task.FromResult(AgentToolResult.Fail($"Task '{task.TaskId}' is marked as running but no active runner found. It may have just completed."));
        }

        runner.InjectMessage(message);
        this.LogMessageInjected(task.TaskId);

        return Task.FromResult(AgentToolResult.Ok($"Message sent to running subagent '{task.TaskId}'. It will be processed on the next round."));
    }

    private Task<AgentToolResult> HandleCompletedTask(SubagentTask task, string message, CancellationToken cancellationToken)
    {
        if (task.Messages.Count == 0)
        {
            return Task.FromResult(AgentToolResult.Fail($"Task '{task.TaskId}' has no stored conversation history (purged after 24h). Cannot resume."));
        }

        // Update state to revising
        this.store.UpdateState(task.TaskId, SubagentTaskState.Revising);

        // Append the new user message to stored history
        var messages = new List<LlmMessage>(task.Messages)
        {
            new() { Role = "user", Content = message },
        };
        this.store.UpdateMessages(task.TaskId, messages, task.Rounds);

        // Check slot availability
        if (!this.registry.HasAvailableSlot)
        {
            this.store.UpdateState(task.TaskId, SubagentTaskState.Queued);
            this.LogResumeQueued(task.TaskId);
            return Task.FromResult(AgentToolResult.Ok($"Subagent '{task.TaskId}' queued for resumption (all concurrency slots in use). " +
                          $"It will resume automatically when a slot opens."));
        }

        this.store.UpdateState(task.TaskId, SubagentTaskState.Running);
        FireResumedRunner(task.TaskId, messages, task.ParentConversation, cancellationToken);

        this.LogResumed(task.TaskId);
        return Task.FromResult(AgentToolResult.Ok($"Subagent '{task.TaskId}' resumed with your follow-up. It will deliver results when done."));
    }

    private void FireResumedRunner(string taskId, List<LlmMessage> messages, string conversationId, CancellationToken cancellationToken)
    {
        var maxRounds = this.agentConfig.CurrentValue.MaxSubagentRounds;
        var runner = new SubagentRunner(
            this.llmClient, this.toolRegistryFactory(), maxRounds, this.logger,
            this.store, taskId, OnRunnerCompletedAsync, this.modelProvider, this.todoStore,
            this.imageAgingOptions, this.imageDescriber);

        if (!this.registry.TryRegister(taskId, runner))
        {
            this.store.UpdateState(taskId, SubagentTaskState.Queued);
            this.LogResumeQueued(taskId);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.ResumeAsync(
                    this.modelProvider.DefaultModel, messages,
                    $"subagent-{taskId}", cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Background task must not crash the process
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogResumeCrashed(taskId, ex.Message);
                this.store.UpdateState(taskId, SubagentTaskState.Failed, result: $"[Subagent crashed: {ex.Message}]");
            }
            finally
            {
                this.registry.Remove(taskId);

                var task = this.store.GetById(taskId);
                if (task?.State == SubagentTaskState.Failed)
                {
                    try
                    {
                        await this.onCompletion(taskId, task.Result ?? "[Subagent crashed]").ConfigureAwait(false);
                    }
#pragma warning disable CA1031
                    catch { /* must not throw */ }
#pragma warning restore CA1031
                }

                // Dequeue next
                var next = this.store.GetOldestQueued();
                if (next is not null && this.registry.HasAvailableSlot)
                {
                    this.store.UpdateState(next.TaskId, SubagentTaskState.Running);
                    FireResumedRunner(next.TaskId, [.. next.Messages], next.ParentConversation, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }

    private async Task OnRunnerCompletedAsync(string taskId, string result)
    {
        this.registry.Remove(taskId);
        await this.onCompletion(taskId, result).ConfigureAwait(false);

        // Dequeue next queued task if any
        var next = this.store.GetOldestQueued();
        if (next is not null && this.registry.HasAvailableSlot)
        {
            this.store.UpdateState(next.TaskId, SubagentTaskState.Running);
            var messages = new List<LlmMessage>(next.Messages);
            if (messages.Count > 0)
            {
                FireResumedRunner(next.TaskId, messages, next.ParentConversation, CancellationToken.None);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_send] Message injected into running task {TaskId}")]
    private partial void LogMessageInjected(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_send] Task {TaskId} resumed with follow-up")]
    private partial void LogResumed(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_send] Task {TaskId} queued for resumption")]
    private partial void LogResumeQueued(string taskId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[sub_agent_send] Runner not found for task {TaskId}")]
    private partial void LogRunnerNotFound(string taskId);

    [LoggerMessage(Level = LogLevel.Error, Message = "[sub_agent_send] Resumed subagent crashed: {TaskId} — {ErrorMessage}")]
    private partial void LogResumeCrashed(string taskId, string errorMessage);
}
