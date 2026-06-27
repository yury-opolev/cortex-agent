using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionEndTool : IAgentTool
{
    private readonly ICodingAgent agent;
    private readonly CodingAgentSessionStore store;

    public CodingSessionEndTool(ICodingAgent agent, CodingAgentSessionStore store)
    {
        this.agent = agent;
        this.store = store;
    }

    public string Name => "coding_session_end";

    public string Description =>
        "Terminate the channel's active external coding-agent session (or a specific sessionId).";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "sessionId": {
              "type": "string",
              "description": "Optional session ID; defaults to the channel's active session."
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

            var response = await this.agent.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            this.store.MarkEnded(sessionId);

            return CodingToolBase.Ok(new
            {
                sessionId = response.SessionId,
                state = response.State.ToString(),
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
