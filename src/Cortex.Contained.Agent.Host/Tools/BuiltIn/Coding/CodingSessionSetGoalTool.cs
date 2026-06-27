using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionSetGoalTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionSetGoalTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_set_goal";

    public string Description =>
        "Set, replace, or CLEAR a coding session's autonomous goal (and optional budget). With a " +
        "goal set, Coda keeps working autonomously until a judge declares the goal met or the " +
        "budget is exhausted. Coda does NOT merge — always include the full goal text when changing " +
        "the budget. Pass an empty goal (or omit it) to CLEAR the goal and return to interactive " +
        "mode. The new configuration takes effect from the next coding_session_send. Only use this " +
        "when the user explicitly asks for (or to stop) autonomous/goal-driven execution.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "sessionId": {
              "type": "string",
              "description": "Session ID to configure. Defaults to the channel's active session."
            },
            "goal": {
              "type": "string",
              "description": "The autonomous objective. Empty or omitted CLEARS the goal (disables autonomous mode)."
            },
            "maxDuration": {
              "type": "string",
              "description": "Optional wall-clock budget in suffix form (e.g. '30m', '2h', '1d'). Omit to keep Coda's default."
            },
            "maxContinuations": {
              "type": "integer",
              "description": "Optional max number of autonomous continuations. Omit to keep Coda's default."
            }
          }
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            string? sessionId = root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString()
                : null;
            var (resolvedSessionId, resolveError) = CodingToolBase.ResolveSessionId(this.store, context.ChannelId, sessionId);
            if (resolveError is not null)
            {
                return resolveError;
            }

            // Empty/whitespace (or absent) goal clears the goal — coda's session/setGoal treats
            // null/empty as "clear". We normalize to null so the intent is unambiguous downstream.
            var goalRaw = root.TryGetProperty("goal", out var goalEl) && goalEl.ValueKind == JsonValueKind.String
                ? goalEl.GetString()
                : null;
            var goal = string.IsNullOrWhiteSpace(goalRaw) ? null : goalRaw;

            var maxDuration = root.TryGetProperty("maxDuration", out var durEl) && durEl.ValueKind == JsonValueKind.String
                ? durEl.GetString()
                : null;

            int? maxContinuations = root.TryGetProperty("maxContinuations", out var contEl) && contEl.ValueKind == JsonValueKind.Number
                ? contEl.GetInt32()
                : null;

            var response = await this.agent.SetGoalAsync(
                new CodingSetGoalRequest
                {
                    SessionId = resolvedSessionId!,
                    Goal = goal,
                    MaxDuration = maxDuration,
                    MaxContinuations = maxContinuations,
                },
                cancellationToken).ConfigureAwait(false);

            return CodingToolBase.Ok(new
            {
                sessionId = response.SessionId,
                goal = response.Goal,
                maxDuration = response.MaxDuration,
                maxContinuations = response.MaxContinuations,
                cleared = response.Goal is null,
            });
        }
        catch (JsonException ex)
        {
            return CodingToolBase.Error("invalid_params", $"Invalid arguments: {ex.Message}");
        }
        catch (Exception ex)
        {
            return CodingToolBase.FromException(ex);
        }
    }
}
