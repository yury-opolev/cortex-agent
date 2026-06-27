using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Reminders;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Voice-only: cancel a pending delayed speech cue previously scheduled with
/// <c>speak_after_delay</c>. Returns <c>{"cancelled":true}</c> when the cue
/// was found and cancelled, <c>{"cancelled":false}</c> when the id is unknown
/// (already fired, never existed, or just cancelled by another path) —
/// the latter is not a tool error.
/// </summary>
internal sealed class CancelDelayedSpeechTool : IAgentTool
{
    private readonly SessionReminderService reminderService;

    public CancelDelayedSpeechTool(SessionReminderService reminderService)
    {
        this.reminderService = reminderService;
    }

    public string Name => "cancel_delayed_speech";

    public string Description =>
        "Cancel a pending cue by id (returned from speak_after_delay). " +
        "Returns {\"cancelled\":true} if found, {\"cancelled\":false} if unknown " +
        "(already fired, never existed, or just cancelled) — the latter is not an error.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "cue_id": {
              "type": "string",
              "description": "The id returned from speak_after_delay."
            }
          },
          "required": ["cue_id"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.ConversationId.StartsWith(SessionReminderService.VoiceConversationPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AgentToolResult.Fail("cancel_delayed_speech is only available in Discord voice conversations."));
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("cue_id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(AgentToolResult.Fail("Missing or invalid required parameter: cue_id (string)."));
            }

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                return Task.FromResult(AgentToolResult.Fail("cue_id cannot be empty."));
            }

            var cancelled = this.reminderService.Cancel(id!);
            var content = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"cancelled\":{(cancelled ? "true" : "false")}}}");

            return Task.FromResult(AgentToolResult.Ok(content));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}"));
        }
    }
}
