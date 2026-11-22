using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>Honest per-line labels for recall output (§9.2, §5.3).</summary>
public enum HistoryLineLabel
{
    /// <summary>A user message.</summary>
    User,

    /// <summary>A normal assistant reply.</summary>
    Assistant,

    /// <summary>An agent-initiated message that was sent to a channel (Proactive).</summary>
    Delivered,

    /// <summary>A system/error/slash-command row.</summary>
    System,

    /// <summary>Scheduled-task instructions, worker summaries, subagent completions.</summary>
    Internal,

    /// <summary>An assistant reply from a turn that used a <c>history_*</c> tool.</summary>
    RecallEcho,
}

/// <summary>Maps stored rows to <see cref="HistoryLineLabel"/> and display text.</summary>
public static class HistoryLabels
{
    /// <summary>The label for <paramref name="row"/>.</summary>
    public static HistoryLineLabel For(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Category switch
        {
            MessageCategory.Internal => HistoryLineLabel.Internal,
            MessageCategory.System or MessageCategory.Transfer => HistoryLineLabel.System,
            MessageCategory.Proactive => HistoryLineLabel.Delivered,
            _ when string.Equals(row.Role, "user", StringComparison.OrdinalIgnoreCase) => HistoryLineLabel.User,
            _ when string.Equals(row.Role, "assistant", StringComparison.OrdinalIgnoreCase) =>
                UsedRecallTool(row.ToolCalls) ? HistoryLineLabel.RecallEcho : HistoryLineLabel.Assistant,
            _ => HistoryLineLabel.System,
        };
    }

    /// <summary>The label text shown to the agent.</summary>
    public static string Text(HistoryLineLabel label) => label switch
    {
        HistoryLineLabel.User => "user",
        HistoryLineLabel.Assistant => "assistant",
        HistoryLineLabel.Delivered => "delivered",
        HistoryLineLabel.System => "system",
        HistoryLineLabel.Internal => "internal (not necessarily delivered)",
        HistoryLineLabel.RecallEcho => "recall_echo",
        _ => "system",
    };

    /// <summary>True for the recall tool family (<c>history_*</c>).</summary>
    public static bool IsRecallTool(string toolName) => ToolCallSummary.IsRecallTool(toolName);

    // Per-turn (§9.2): a row carries either a history_* tool entry itself or the synthetic turn
    // marker added to every other assistant row of a turn that used one (which also matches here).
    private static bool UsedRecallTool(string? toolCallsJson) =>
        toolCallsJson is not null && ToolCallSummary.ParseJson(toolCallsJson).Any(e => IsRecallTool(e.Name));
}
