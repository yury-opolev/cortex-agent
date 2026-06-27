using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionStartTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionStartTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_start";

    public string Description =>
        "Start a new Coda coding-agent session in an allowlisted working folder on the host. " +
        "The folder must be in the configured allowlist (Bridge > Settings > Coding). " +
        "The session is non-blocking; messages and results flow through coding_session_send and " +
        "are pushed back as injected envelopes the LLM relays to the user. Pin the session to the current channel.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "workingFolder": {
              "type": "string",
              "description": "Absolute path on the Windows host where the session will run. Must be an allowlisted folder or a child of one."
            },
            "policy": {
              "type": "string",
              "enum": ["Prompt", "YoloSafe", "Yolo"],
              "description": "Optional permission policy. Defaults to the folder's configured policy. May only be MORE restrictive than the folder ceiling (e.g. Prompt when the folder allows YoloSafe). Never specify a more permissive policy than the folder allows."
            },
            "sessionName": {
              "type": "string",
              "description": "Optional display name for the session."
            },
            "goal": {
              "type": "string",
              "description": "Optional autonomous goal: Coda keeps running until this objective is met. Only set when the user explicitly asks for autonomous/goal-driven execution."
            },
            "sessionMemory": {
              "type": "boolean",
              "description": "When true, enable Coda's session-memory feature. Only set when the user explicitly requests it.",
              "default": false
            }
          },
          "required": ["workingFolder"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("workingFolder", out var folderEl) || folderEl.ValueKind != JsonValueKind.String)
            {
                return CodingToolBase.Error("invalid_params", "workingFolder (string) is required.");
            }

            var workingFolder = folderEl.GetString()!;

            // Parse optional policy string (case-insensitive). Legacy yolo bool maps to Yolo policy.
            CodingPolicy? requestedPolicy = null;
            if (root.TryGetProperty("policy", out var policyEl) && policyEl.ValueKind == JsonValueKind.String)
            {
                var policyStr = policyEl.GetString();
                if (!Enum.TryParse<CodingPolicy>(policyStr, ignoreCase: true, out var parsed))
                {
                    return CodingToolBase.Error("invalid_params", $"Unknown policy '{policyStr}'. Valid values: Prompt, YoloSafe, Yolo.");
                }

                requestedPolicy = parsed;
            }
            else if (root.TryGetProperty("yolo", out var yoloEl) && yoloEl.ValueKind == JsonValueKind.True)
            {
                // Back-compat: legacy yolo=true maps to Yolo policy.
                requestedPolicy = CodingPolicy.Yolo;
            }

            var sessionName = root.TryGetProperty("sessionName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString()
                : null;
            var goal = root.TryGetProperty("goal", out var goalEl) && goalEl.ValueKind == JsonValueKind.String
                ? goalEl.GetString()
                : null;
            var sessionMemory = root.TryGetProperty("sessionMemory", out var smEl) && smEl.ValueKind == JsonValueKind.True;

            var channelId = CodingToolBase.ResolveChannelId(context, root);
            if (channelId is null)
            {
                return CodingToolBase.Error("invalid_params", "Channel context is required to start an coding agent session.");
            }

            var status = await this.agent.StartSessionAsync(
                new CodingStartRequest
                {
                    ChannelId = channelId,
                    WorkingFolder = workingFolder,
                    RequestedPolicy = requestedPolicy,
                    SessionName = sessionName,
                    Goal = goal,
                    SessionMemory = sessionMemory,
                },
                cancellationToken).ConfigureAwait(false);

            this.store.Upsert(CodingToolBase.ToRecord(status));

            return CodingToolBase.Ok(CodingToolBase.SnapshotPayload(status));
        }
        catch (JsonException ex)
        {
            return CodingToolBase.Error("invalid_params", $"Invalid arguments: {ex.Message}");
        }
        catch (CodingInvokeException ex)
        {
            // State-bearing failure: the message already says whether a session is running.
            return CodingToolBase.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // CodingInvokeException carries the stable coda_* code; everything else is internal_error.
            return CodingToolBase.FromException(ex);
        }
    }
}
