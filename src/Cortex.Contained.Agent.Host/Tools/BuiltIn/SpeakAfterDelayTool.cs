using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Reminders;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Voice-only: schedules a pre-composed utterance to be spoken aloud in the
/// active Discord voice conversation after a fixed delay. The text is frozen
/// at schedule time — no LLM round-trip occurs when the timer fires.
/// See <see cref="SessionReminderService"/>.
/// </summary>
internal sealed class SpeakAfterDelayTool : IAgentTool
{
    private readonly SessionReminderService reminderService;

    public SpeakAfterDelayTool(SessionReminderService reminderService)
    {
        this.reminderService = reminderService;
    }

    public string Name => "speak_after_delay";

    public string Description =>
        "Schedule a brief utterance to be spoken aloud after `delay_seconds` " +
        $"(range {SessionReminderService.MinDelaySeconds}-{SessionReminderService.MaxDelaySeconds}). " +
        "The `text` you supply is spoken verbatim at fire time (no LLM round-trip). " +
        "Use for short pacing cues during the live session — set timers, kitchen timers, brief check-ins. " +
        "For scheduled-time or recurring tasks, use `schedule_task` instead. " +
        "Returns a `cue_id` for `cancel_delayed_speech`.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "delay_seconds": {
              "type": "integer",
              "description": "Delay in seconds before the cue is spoken. Range {{SessionReminderService.MinDelaySeconds}}-{{SessionReminderService.MaxDelaySeconds}}."
            },
            "text": {
              "type": "string",
              "description": "The exact words to speak when the timer fires. Must be non-empty."
            }
          },
          "required": ["delay_seconds", "text"]
        }
        """;

    public Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!context.ConversationId.StartsWith(SessionReminderService.VoiceConversationPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AgentToolResult.Fail("speak_after_delay is only available in Discord voice conversations."));
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("delay_seconds", out var delayElement)
                || delayElement.ValueKind != JsonValueKind.Number
                || !delayElement.TryGetInt32(out var delaySeconds))
            {
                return Task.FromResult(AgentToolResult.Fail("Missing or invalid required parameter: delay_seconds (integer)."));
            }

            if (!root.TryGetProperty("text", out var textElement)
                || textElement.ValueKind != JsonValueKind.String)
            {
                return Task.FromResult(AgentToolResult.Fail("Missing or invalid required parameter: text (non-empty string)."));
            }

            var text = textElement.GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult(AgentToolResult.Fail("text cannot be empty or whitespace."));
            }

            string cueId;
            try
            {
                cueId = this.reminderService.Schedule(context.ConversationId, context.ChannelId, delaySeconds, text);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Task.FromResult(AgentToolResult.Fail($"delay_seconds must be between {SessionReminderService.MinDelaySeconds} and {SessionReminderService.MaxDelaySeconds} (got {delaySeconds})."));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(AgentToolResult.Fail(ex.Message));
            }

            var content = string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"cue_id\":\"{cueId}\"}}");

            return Task.FromResult(AgentToolResult.Ok(content));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}"));
        }
    }
}
