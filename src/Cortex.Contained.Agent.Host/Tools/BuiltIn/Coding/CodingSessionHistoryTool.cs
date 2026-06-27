using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

/// <summary>
/// Fetches a coding session's transcript — the full conversation, or only the messages after a
/// cursor (<c>sinceIndex</c>) for incremental polling. Wraps the Bridge → coda
/// <c>session/history</c> / <c>session/messages</c> calls.
/// </summary>
internal sealed class CodingSessionHistoryTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionHistoryTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_history";

    public string Description =>
        "Fetch the transcript of an external coding-agent session as a list of {role, content} " +
        "messages. Omit sinceIndex for the full conversation; pass sinceIndex to get only the " +
        "messages after that cursor (the result's nextIndex is the cursor to pass next time). " +
        "Use this to read what the coding agent actually said or did.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "sessionId": {
              "type": "string",
              "description": "Session ID to read. Defaults to the channel's active session."
            },
            "sinceIndex": {
              "type": "integer",
              "description": "Optional cursor: return only messages after this index (use the previous result's nextIndex). Omit for the full transcript."
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

            int? sinceIndex = root.TryGetProperty("sinceIndex", out var sinceEl) && sinceEl.ValueKind == JsonValueKind.Number
                ? sinceEl.GetInt32()
                : null;

            var (resolvedSessionId, resolveError) = CodingToolBase.ResolveSessionId(this.store, context.ChannelId, sessionId);
            if (resolveError is not null)
            {
                return resolveError;
            }

            sessionId = resolvedSessionId!;

            var history = await this.agent.GetHistoryAsync(sessionId, sinceIndex, cancellationToken).ConfigureAwait(false);

            return CodingToolBase.Ok(new
            {
                sessionId,
                messages = history.Messages.Select(m => new { role = m.Role, content = m.Content }),
                nextIndex = history.NextIndex,
            });
        }
        catch (JsonException ex)
        {
            return CodingToolBase.Error("invalid_params", $"Invalid arguments: {ex.Message}");
        }
        catch (CodingInvokeException ex)
        {
            return CodingToolBase.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // CodingInvokeException carries the stable coda_* code; everything else is internal_error.
            return CodingToolBase.FromException(ex);
        }
    }
}
