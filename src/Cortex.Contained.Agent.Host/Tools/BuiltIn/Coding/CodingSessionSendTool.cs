using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionSendTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionSendTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_send";

    public string Description =>
        "Send a user instruction to the external coding agent's active session for this channel. " +
        "If the session is idle this starts a new task and returns a taskId; the result is pushed back " +
        "later as an [coding ...] envelope. If the session is mid-turn (working), the message is instead " +
        "delivered as a STEERING comment that redirects the running turn — the response has steered=true, " +
        "reuses the running turn's taskId, and produces NO separate result envelope (the redirected turn's " +
        "single envelope still carries that taskId).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "message": {
              "type": "string",
              "description": "Instruction text to forward to the coding agent."
            },
            "sessionId": {
              "type": "string",
              "description": "Optional session ID. If omitted, defaults to the channel's active session."
            }
          },
          "required": ["message"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("message", out var msgEl) || msgEl.ValueKind != JsonValueKind.String)
            {
                return CodingToolBase.Error("invalid_params", "message (string) is required.");
            }

            var message = msgEl.GetString()!;
            string? sessionId = root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString()
                : null;

            var (resolvedSessionId, resolveError) = CodingToolBase.ResolveSessionId(this.store, context.ChannelId, sessionId);
            if (resolveError is not null)
            {
                return resolveError;
            }

            sessionId = resolvedSessionId!;

            var response = await this.agent.SendMessageAsync(
                new CodingSendRequest { SessionId = sessionId, Message = message },
                cancellationToken).ConfigureAwait(false);

            // Update last_user_message in store.
            var record = this.store.GetById(sessionId);
            if (record is not null)
            {
                this.store.Upsert(record with
                {
                    LastUserMessage = message.Length <= 500 ? message : message[..500] + "…",
                    LastActivityAt = DateTimeOffset.UtcNow,
                    State = response.State,
                });
            }

            return CodingToolBase.Ok(new
            {
                taskId = response.TaskId,
                sessionId = response.SessionId,
                state = response.State.ToString(),
                steered = response.Steered,
            });
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
