using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionStatusTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionStatusTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_status";

    public string Description =>
        "Get the current status of an external coding-agent session, including its state, " +
        "working folder, last user message, last assistant summary, and last 5 tool calls. " +
        "Use this to answer the user's questions like 'what did claude change?'.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "sessionId": {
              "type": "string",
              "description": "Session ID to inspect. Defaults to the channel's active session."
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

            sessionId = resolvedSessionId!;

            var status = await this.agent.GetStatusAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                // Fall back to local store
                var record = this.store.GetById(sessionId);
                if (record is null)
                {
                    return CodingToolBase.Error(CodingBridgeErrorCodes.SessionUnknown, $"No session {sessionId}.");
                }

                return CodingToolBase.Ok(new
                {
                    sessionId = record.SessionId,
                    channelId = record.ChannelId,
                    workingFolder = record.WorkingFolder,
                    state = record.State.ToString(),
                    policy = record.Policy.ToString(),
                    sessionName = record.SessionName,
                    createdAt = record.CreatedAt,
                    lastActivityAt = record.LastActivityAt,
                    lastUserMessage = record.LastUserMessage,
                    lastAssistantSummary = record.LastAssistantSummary,
                    lastToolCalls = CodingAgentSessionStore.DeserializeToolCalls(record.LastToolCallsJson),
                });
            }

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
