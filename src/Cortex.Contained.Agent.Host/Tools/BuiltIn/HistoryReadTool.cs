using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Reads a slice of one channel's conversation as a Visitor/Consultant transcript,
/// including tool-use blocks rendered beneath assistant turns. Supports time-range
/// and row-count slicing.
/// </summary>
internal sealed class HistoryReadTool : IAgentTool
{
    private const int DefaultLimit = 200;
    private const int MinLimit = 1;
    private const int MaxLimit = 1000;

    /// <summary>Maximum characters per individual message before truncation in the formatted transcript.</summary>
    private const int MaxMessageChars = 3_000;

    /// <summary>Hard cap on total characters of formatted conversation text.</summary>
    private const int MaxConversationChars = 500_000;

    private readonly MessageStore messageStore;

    public HistoryReadTool(MessageStore messageStore)
    {
        this.messageStore = messageStore;
    }

    public string Name => "history_read";

    public string Description =>
        "Read past conversation messages from one channel as a Visitor/Consultant transcript. " +
        "Use history_list_channels first to discover channelId values. " +
        "Optional 'since' (inclusive) and 'until' (exclusive) ISO 8601 bounds slice the range; " +
        $"'limit' caps row count (default {DefaultLimit}, max {MaxLimit}). " +
        "Returns the formatted transcript with [N] indices on Visitor/Consultant turns and " +
        "'Tools used' blocks under assistant turns. Returns an empty string for an unknown " +
        "channel or an empty range — not an error.";

    public string ParametersSchema => $$"""
        {
          "type": "object",
          "properties": {
            "channelId": {
              "type": "string",
              "description": "Channel identifier from history_list_channels (e.g. 'webchat-default')."
            },
            "since": {
              "type": "string",
              "description": "Inclusive lower bound on message timestamps, ISO 8601 (e.g. '2026-05-04T00:00:00Z'). Optional."
            },
            "until": {
              "type": "string",
              "description": "Exclusive upper bound on message timestamps, ISO 8601. Optional."
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of messages to read. Default {{DefaultLimit}}, range {{MinLimit}}-{{MaxLimit}}."
            }
          },
          "required": ["channelId"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("channelId", out var channelIdElement))
            {
                return AgentToolResult.Fail("Missing required parameter: channelId");
            }

            var channelId = channelIdElement.GetString();
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return AgentToolResult.Fail("channelId cannot be empty.");
            }

            DateTimeOffset? since = null;
            if (root.TryGetProperty("since", out var sinceElement) && sinceElement.ValueKind == JsonValueKind.String)
            {
                var raw = sinceElement.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        return AgentToolResult.Fail($"Invalid 'since' format: '{raw}'. Use ISO 8601 (e.g. '2026-05-04T00:00:00Z').");
                    }
                    since = parsed;
                }
            }

            DateTimeOffset? until = null;
            if (root.TryGetProperty("until", out var untilElement) && untilElement.ValueKind == JsonValueKind.String)
            {
                var raw = untilElement.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        return AgentToolResult.Fail($"Invalid 'until' format: '{raw}'. Use ISO 8601 (e.g. '2026-05-04T00:00:00Z').");
                    }
                    until = parsed;
                }
            }

            var limit = DefaultLimit;
            if (root.TryGetProperty("limit", out var limitElement) && limitElement.ValueKind == JsonValueKind.Number)
            {
                if (!limitElement.TryGetInt32(out limit))
                {
                    return AgentToolResult.Fail($"limit must be an integer between {MinLimit} and {MaxLimit}.");
                }

                if (limit < MinLimit || limit > MaxLimit)
                {
                    return AgentToolResult.Fail($"limit must be between {MinLimit} and {MaxLimit} (got {limit}).");
                }
            }

            // until <= since: empty window, succeed with empty content (no DB call needed).
            if (since.HasValue && until.HasValue && until.Value <= since.Value)
            {
                return AgentToolResult.Ok(string.Empty);
            }

            var records = await this.messageStore.GetMessagesAsync(
                channelId!,
                limit,
                before: until,
                after: since,
                visibility: MessageVisibility.Seeding,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (records.Count == 0)
            {
                return AgentToolResult.Ok(string.Empty);
            }

            var formatted = FormatChannelText(records);
            return AgentToolResult.Ok(formatted);
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }
    }

    /// <summary>
    /// Formats records as a Visitor/Consultant transcript. Indices apply only to
    /// non-tool roles. Tool-call summaries persisted on assistant records are
    /// rendered as a multi-line block beneath the originating Consultant turn and
    /// never receive their own index.
    /// </summary>
    private static string FormatChannelText(List<MessageRecord> records)
    {
        var sb = new StringBuilder();
        var messageIndex = 0;

        foreach (var record in records)
        {
            if (string.Equals(record.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = ConversationPreprocessor.SanitizeForLlm(record.Content, MaxMessageChars);
            var role = string.Equals(record.Role, "user", StringComparison.OrdinalIgnoreCase)
                ? "Visitor"
                : "Consultant";
            sb.AppendLine(CultureInfo.InvariantCulture, $"[{messageIndex}] {role}: {text}");

            if (string.Equals(record.Role, "assistant", StringComparison.OrdinalIgnoreCase) && record.ToolCalls is not null)
            {
                var entries = ToolCallSummary.ParseJson(record.ToolCalls);
                if (entries.Count > 0)
                {
                    sb.AppendLine(ToolCallSummary.RenderBlock(entries));
                }
            }

            messageIndex++;

            if (sb.Length >= MaxConversationChars)
            {
                sb.AppendLine("... (truncated)");
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Test-only entry point for <see cref="FormatChannelText"/>. Exposes the
    /// private formatter to the unit-test assembly via InternalsVisibleTo.
    /// </summary>
    internal static string FormatChannelTextForTesting(List<MessageRecord> records)
        => FormatChannelText(records);
}
