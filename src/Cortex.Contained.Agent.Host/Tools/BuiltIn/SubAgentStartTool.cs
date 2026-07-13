using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Spawns an async subagent that runs in the background. Persists a durable, queued task record
/// (RunMode.New) and signals <see cref="SubagentExecutionCoordinator"/>, which claims, admits under
/// the concurrency cap, and executes it. Returns a task_id immediately so the main agent can respond
/// to the user without waiting.
/// </summary>
public sealed partial class SubAgentStartTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly SubagentExecutionCoordinator coordinator;
    private readonly ILogger<SubAgentStartTool> logger;

    public SubAgentStartTool(
        SubagentSessionStore store,
        SubagentExecutionCoordinator coordinator,
        ILogger<SubAgentStartTool> logger)
    {
        this.store = store;
        this.coordinator = coordinator;
        this.logger = logger;
    }

    public string Name => "sub_agent_start";

    public string Description =>
        "Spawn an async subagent to perform a multi-step task in the background. " +
        "Returns a task_id immediately. When the task completes, you will receive a " +
        "[Background task completed] message with the results to review. " +
        "In the prompt, describe the task and tell to respond with results. " +
        "Never ask the subagent to send, deliver, or message the user. " +
        "Use this for complex, multi-step work that would require many tool calls " +
        "(e.g., researching across multiple files, writing and revising a document, " +
        "performing a series of file operations). " +
        "Do NOT use for simple tasks that need only 1-2 tool calls — do those directly. " +
        "Use sub_agent_read to check status, sub_agent_send to provide additional input.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "description": {
              "type": "string",
              "description": "A short (3-5 words) description of the task"
            },
            "prompt": {
              "type": "string",
              "description": "Detailed instructions for the subagent. Be specific about what to do and what to return."
            },
            "skill": {
              "type": "string",
              "description": "Optional skill name. The skill's SKILL.md content is prepended to the subagent's system prompt for structured guidance."
            }
          },
          "required": ["description", "prompt"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string description;
        string prompt;
        string? skillName;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            description = root.GetProperty("description").GetString() ?? string.Empty;
            prompt = root.GetProperty("prompt").GetString() ?? string.Empty;
            skillName = root.TryGetProperty("skill", out var skillProp)
                ? skillProp.GetString()
                : null;
        }
#pragma warning disable CA1031 // Bad arguments should not crash the agent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(AgentToolResult.Fail("Missing required parameter: prompt"));
        }

        var taskId = string.Create(CultureInfo.InvariantCulture, $"sa-{Guid.NewGuid():N}");

        // Persist a durable, queued task. The coordinator owns admission + execution.
        var task = new SubagentTask
        {
            TaskId = taskId,
            ParentConversation = context.ConversationId,
            ParentChannel = context.ChannelId,
            Description = description,
            Prompt = prompt,
            State = SubagentTaskState.Queued,
            RunMode = SubagentRunMode.New,
            SkillName = skillName,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this.store.Create(task);

        // Wake the coordinator; it starts the task as soon as a slot is free and readiness is satisfied.
        this.coordinator.SignalWorkAvailable();

        this.LogSubAgentQueued(taskId, description);
        return Task.FromResult(AgentToolResult.Ok(
            $"Subagent accepted.\n" +
            $"Task ID: {taskId}\n\n" +
            $"It runs in the background and starts as soon as a concurrency slot is free. " +
            $"You will receive a [Background task completed] message when it finishes. " +
            $"Use sub_agent_read('{taskId}') to check progress."));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Queued: {TaskId} — {Description}")]
    private partial void LogSubAgentQueued(string taskId, string description);
}
