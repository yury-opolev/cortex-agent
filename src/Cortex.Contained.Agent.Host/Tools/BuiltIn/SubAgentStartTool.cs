using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Agent.Autonomy;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Spawns an async subagent that runs in the background. Persists a durable, queued task record
/// (RunMode.New) and signals <see cref="SubagentExecutionCoordinator"/>, which claims, admits under
/// the concurrency cap, and executes it. Returns a task_id immediately so the main agent can respond
/// to the user without waiting.
/// </summary>
public sealed partial class SubAgentStartTool : IAgentTool
{
    /// <summary>
    /// How deep delegation may nest. Bounds the tree so a runaway cannot fan out indefinitely,
    /// while still letting a multi-day goal split work a couple of levels down.
    /// </summary>
    internal const int MaxDelegationDepth = 3;

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
        "[Background task completed] message with the results to review, or a " +
        "[Background task FAILED] / [Background task cancelled] message if it did not " +
        "finish. " +
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
            },
            "goal": {
              "type": "string",
              "description": "Optional autonomous goal for a long-running task delegated to a subagent. Unlike coding sessions where goal mode is off by default, this is the expected mode when the user asks for long-running or explicitly autonomous work: the subagent keeps going until this objective is met, genuinely blocked, stalled, or budget-exhausted."
            },
            "maxDuration": {
              "type": "string",
              "description": "Optional wall-clock budget for an autonomous goal, such as 90s, 30m, 2h, 7d, or none/unlimited/off. Defaults to 7d when goal is set."
            },
            "maxContinuations": {
              "type": ["string", "integer"],
              "description": "Optional continuation budget for an autonomous goal, as a positive whole number or none/unlimited/off. Defaults to 10000 when goal is set."
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
        string? goal;
        TimeSpan? goalMaxDuration = null;
        int? goalMaxContinuations = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            description = root.GetProperty("description").GetString() ?? string.Empty;
            prompt = root.GetProperty("prompt").GetString() ?? string.Empty;
            skillName = root.TryGetProperty("skill", out var skillProp)
                ? skillProp.GetString()
                : null;
            goal = root.TryGetProperty("goal", out var goalProp)
                ? goalProp.GetString()
                : null;

            if (root.TryGetProperty("maxDuration", out var maxDurationProp))
            {
                if (maxDurationProp.ValueKind != JsonValueKind.String)
                {
                    return Task.FromResult(AgentToolResult.Fail("Invalid maxDuration: expected a duration string like 90s, 30m, 2h, 7d, or none."));
                }

                try
                {
                    goalMaxDuration = GoalBudget.ParseWallClockLimit(maxDurationProp.GetString() ?? string.Empty);
                }
                catch (FormatException ex)
                {
                    return Task.FromResult(AgentToolResult.Fail($"Invalid maxDuration: {ex.Message}"));
                }
            }

            if (root.TryGetProperty("maxContinuations", out var maxContinuationsProp))
            {
                try
                {
                    goalMaxContinuations = GoalBudget.ParseContinuationLimit(ReadContinuationLimit(maxContinuationsProp));
                }
                catch (FormatException ex)
                {
                    return Task.FromResult(AgentToolResult.Fail($"Invalid maxContinuations: {ex.Message}"));
                }
            }
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

        if (!string.IsNullOrWhiteSpace(goal))
        {
            goalMaxDuration ??= GoalBudget.DefaultWallClockLimit;
            goalMaxContinuations ??= GoalBudget.DefaultContinuationLimit;

            try
            {
                _ = GoalBudget.Create(TimeProvider.System, goalMaxDuration, goalMaxContinuations);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Task.FromResult(AgentToolResult.Fail($"Invalid autonomous goal budget: {ex.Message}"));
            }
        }
        else
        {
            goal = null;
            goalMaxDuration = null;
            goalMaxContinuations = null;
        }

        var taskId = string.Create(CultureInfo.InvariantCulture, $"sa-{Guid.NewGuid():N}");

        // Nesting: when the caller is itself a subagent, this task is its child. Depth is what
        // bounds the tree and what depth-first claiming uses to keep it from deadlocking.
        string? parentTaskId = null;
        var depth = 0;
        if (SubagentConversationIds.TryGetTaskId(context.ConversationId, out var callerTaskId))
        {
            var parent = this.store.GetById(callerTaskId);
            if (parent is not null)
            {
                parentTaskId = parent.TaskId;
                depth = parent.Depth + 1;

                if (depth > MaxDelegationDepth)
                {
                    return Task.FromResult(AgentToolResult.Fail(
                        $"Delegation depth limit reached ({MaxDelegationDepth}). Do this work yourself rather than delegating further."));
                }
            }
        }

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
            Goal = goal,
            GoalMaxDuration = goalMaxDuration,
            GoalMaxContinuations = goalMaxContinuations,
            CreatedAt = DateTimeOffset.UtcNow,
            Depth = depth,
            ParentTaskId = parentTaskId,
        };
        this.store.Create(task);

        // Wake the coordinator; it starts the task as soon as a slot is free and readiness is satisfied.
        this.coordinator.SignalWorkAvailable();

        this.LogSubAgentQueued(taskId, description);
        return Task.FromResult(AgentToolResult.Ok(
            $"Subagent accepted.\n" +
            $"Task ID: {taskId}\n\n" +
            $"It runs in the background and starts as soon as a concurrency slot is free. " +
            $"You will receive a [Background task completed] message when it finishes, or " +
            $"[Background task FAILED] / [Background task cancelled] if it does not. A failed " +
            $"task keeps the work it already did - resume it with sub_agent_send('{taskId}', ...) " +
            $"rather than starting it over. " +
            $"Use sub_agent_read('{taskId}') to check progress."));
    }

    private static string ReadContinuationLimit(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number when element.TryGetInt32(out var value) => value.ToString(CultureInfo.InvariantCulture),
            _ => throw new FormatException("maxContinuations must be a positive whole number or none."),
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_start] Queued: {TaskId} — {Description}")]
    private partial void LogSubAgentQueued(string taskId, string description);
}
