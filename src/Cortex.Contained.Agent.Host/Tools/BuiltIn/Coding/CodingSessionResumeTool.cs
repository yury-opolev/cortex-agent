using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionResumeTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionResumeTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_resume";

    public string Description =>
        "Re-attach to an existing external coding-agent session by ID and pin it to this channel.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "sessionId": {
              "type": "string",
              "description": "Session ID emitted from a previous coda run."
            },
            "workingFolder": {
              "type": "string",
              "description": "Absolute path on the Windows host. Must match the original session's folder and be an allowlisted folder or a child of one."
            },
            "policy": {
              "type": "string",
              "enum": ["Prompt", "YoloSafe", "Yolo"],
              "description": "Optional permission policy. Defaults to the folder's configured policy. May only be MORE restrictive than the folder ceiling."
            }
          },
          "required": ["sessionId", "workingFolder"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("sessionId", out var sidEl) || sidEl.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("workingFolder", out var folderEl) || folderEl.ValueKind != JsonValueKind.String)
            {
                return CodingToolBase.Error("invalid_params", "sessionId and workingFolder (strings) are required.");
            }

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

            var channelId = CodingToolBase.ResolveChannelId(context, root);
            if (channelId is null)
            {
                return CodingToolBase.Error("invalid_params", "Channel context is required.");
            }

            var status = await this.agent.ResumeSessionAsync(
                new CodingResumeRequest
                {
                    ChannelId = channelId,
                    SessionId = sidEl.GetString()!,
                    WorkingFolder = folderEl.GetString()!,
                    RequestedPolicy = requestedPolicy,
                },
                cancellationToken).ConfigureAwait(false);

            this.store.Upsert(CodingToolBase.ToRecord(status));
            return CodingToolBase.Ok(CodingToolBase.SnapshotPayload(status));
        }
        catch (JsonException ex)
        {
            return CodingToolBase.Error("invalid_params", $"Invalid arguments: {ex.Message}");
        }
        catch (Exception ex)
        {
            // CodingInvokeException carries the stable coda_* code; everything else is internal_error.
            return CodingToolBase.FromException(ex);
        }
    }
}
