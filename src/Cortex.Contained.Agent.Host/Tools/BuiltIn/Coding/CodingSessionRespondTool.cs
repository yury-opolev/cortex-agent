using System.Text.Json;
using Cortex.Contained.Agent.Host.Coding;
using Cortex.Contained.Contracts.Coding;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn.Coding;

internal sealed class CodingSessionRespondTool : IAgentTool
{
    private readonly ICodingAgent agent;

    public CodingSessionRespondTool(ICodingAgent agent)
    {
        this.agent = agent;
    }

    public string Name => "coding_session_respond";

    public string Description =>
        "Reply to a pending permission ask, question, or plan-approval request from the coding agent. " +
        "Pass the requestId from the [coding ...] envelope and the response: " +
        "'allow_once', 'allow_always', or 'deny' for permission; " +
        "the chosen option or free-form text for questions; " +
        "'approve' or 'reject' for plan approvals.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "requestId": {
              "type": "string",
              "description": "Request ID from the awaiting envelope."
            },
            "response": {
              "type": "string",
              "description": "'allow_once' | 'allow_always' | 'deny' | 'approve' | 'reject' | free-form text."
            }
          },
          "required": ["requestId", "response"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("requestId", out var ridEl) || ridEl.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("response", out var respEl) || respEl.ValueKind != JsonValueKind.String)
            {
                return CodingToolBase.Error("invalid_params", "requestId and response (strings) are required.");
            }

            await this.agent.RespondAsync(
                new CodingRespondRequest { RequestId = ridEl.GetString()!, Response = respEl.GetString()! },
                cancellationToken).ConfigureAwait(false);

            return CodingToolBase.Ok(new { requestId = ridEl.GetString(), accepted = true });
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
