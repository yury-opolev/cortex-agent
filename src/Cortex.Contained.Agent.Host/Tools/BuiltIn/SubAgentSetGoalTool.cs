using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Agent.Autonomy;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Sets, replaces or CLEARS a subagent's autonomous goal while it is running.
/// <para>
/// Modelled on <c>coding_session_set_goal</c>, including its two hard-won rules: the update does
/// NOT merge (always send the full goal text when changing a budget, or a partial update would
/// silently turn an autonomous run into an ordinary one), and it takes effect at the next loop
/// boundary rather than mid-round.
/// </para>
/// </summary>
public sealed partial class SubAgentSetGoalTool : IAgentTool
{
    private readonly SubagentSessionStore store;
    private readonly SubagentRunnerRegistry registry;
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SubAgentSetGoalTool> logger;

    public SubAgentSetGoalTool(
        SubagentSessionStore store,
        SubagentRunnerRegistry registry,
        ILlmClient llmClient,
        IModelProvider modelProvider,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        ILogger<SubAgentSetGoalTool> logger)
    {
        this.store = store;
        this.registry = registry;
        this.llmClient = llmClient;
        this.modelProvider = modelProvider;
        this.loggerFactory = loggerFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public string Name => "sub_agent_set_goal";

    public string Description =>
        "Set, replace, or CLEAR a running subagent's autonomous goal. With a goal set, the " +
        "subagent keeps working until a judge decides the goal is met, it is provably blocked, " +
        "it stalls, or the budget runs out. Pass an empty goal (or omit it) to CLEAR the goal, " +
        "after which the subagent finishes its current pass and stops like an ordinary one. " +
        "Does NOT merge: always include the full goal text when changing a budget. Takes effect " +
        "at the subagent's next loop boundary.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "task_id": {
              "type": "string",
              "description": "The task ID of the subagent to configure."
            },
            "goal": {
              "type": "string",
              "description": "The autonomous objective. Empty or omitted CLEARS the goal."
            },
            "maxDuration": {
              "type": "string",
              "description": "Optional wall-clock budget such as 90s, 30m, 2h, 7d, or none/unlimited/off. Defaults to 7d."
            },
            "maxContinuations": {
              "type": ["string", "integer"],
              "description": "Optional continuation budget as a positive whole number, or none/unlimited/off. Defaults to 10000."
            }
          },
          "required": ["task_id"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(
        string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        string? taskId;
        string? goal;
        TimeSpan? maxDuration = null;
        int? maxContinuations = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            taskId = root.TryGetProperty("task_id", out var idEl) ? idEl.GetString() : null;

            // Empty/whitespace (or absent) clears the goal. Normalised to null so the intent is
            // unambiguous downstream.
            goal = root.TryGetProperty("goal", out var goalEl) && goalEl.ValueKind == JsonValueKind.String
                ? goalEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(goal))
            {
                goal = null;
            }

            if (root.TryGetProperty("maxDuration", out var durationEl))
            {
                if (durationEl.ValueKind != JsonValueKind.String)
                {
                    return Task.FromResult(AgentToolResult.Fail(
                        "Invalid maxDuration: expected a duration string like 90s, 30m, 2h, 7d, or none."));
                }

                maxDuration = GoalBudget.ParseWallClockLimit(durationEl.GetString()!);
            }

            if (root.TryGetProperty("maxContinuations", out var continuationsEl))
            {
                var raw = continuationsEl.ValueKind switch
                {
                    JsonValueKind.String => continuationsEl.GetString()!,
                    JsonValueKind.Number => continuationsEl.GetInt32().ToString(CultureInfo.InvariantCulture),
                    _ => null,
                };

                if (raw is null)
                {
                    return Task.FromResult(AgentToolResult.Fail(
                        "Invalid maxContinuations: expected a positive whole number, or none/unlimited/off."));
                }

                maxContinuations = GoalBudget.ParseContinuationLimit(raw);
            }
        }
        catch (JsonException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid arguments: {ex.Message}"));
        }
        catch (FormatException ex)
        {
            return Task.FromResult(AgentToolResult.Fail(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Task.FromResult(AgentToolResult.Fail("task_id is required."));
        }

        var task = this.store.GetById(taskId);
        if (task is null)
        {
            this.LogTaskNotFound(taskId);
            return Task.FromResult(AgentToolResult.Fail($"No subagent task found with id '{taskId}'."));
        }

        if (task.State is SubagentTaskState.Completed or SubagentTaskState.Failed or SubagentTaskState.Cancelled)
        {
            return Task.FromResult(AgentToolResult.Fail(
                $"Subagent '{taskId}' already finished ({task.State.ToStorageValue()}); its goal cannot be changed."));
        }

        this.store.UpdateGoal(taskId, goal, maxDuration, maxContinuations);

        // Apply to a live runner so a re-aim or stand-down takes effect on the current run rather
        // than only on a future resume. A queued task has no runner yet and picks it up on start.
        var runner = this.registry.TryGet(taskId);
        runner?.SetSupervisor(goal is null ? null : this.BuildSupervisor(task, goal, maxDuration, maxContinuations));

        this.LogGoalUpdated(taskId, goal is null);

        return Task.FromResult(AgentToolResult.Ok(goal is null
            ? $"Cleared the autonomous goal for '{taskId}'. It will finish its current pass and stop."
            : $"Set the autonomous goal for '{taskId}'. Takes effect at its next loop boundary."));
    }

    private AutonomySupervisor BuildSupervisor(
        SubagentTask task,
        string goal,
        TimeSpan? maxDuration,
        int? maxContinuations)
        => new(
            goal,
            GoalBudget.Rehydrate(
                new GoalBudgetConsumed(task.GoalConsumedElapsed, task.GoalConsumedContinuations),
                this.timeProvider,
                maxDuration,
                maxContinuations),
            new CompletionJudge(this.llmClient, this.modelProvider, this.loggerFactory.CreateLogger<CompletionJudge>()),
            new StuckDetector(),
            new AssumptionLedger(),
            this.loggerFactory.CreateLogger<AutonomySupervisor>());

    [LoggerMessage(Level = LogLevel.Information, Message = "[sub_agent_set_goal] Task {TaskId} goal updated (cleared={Cleared})")]
    private partial void LogGoalUpdated(string taskId, bool cleared);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[sub_agent_set_goal] Task not found: {TaskId}")]
    private partial void LogTaskNotFound(string taskId);
}
