using System.Collections.Frozen;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>How much of a tool's payload may be persisted in its events.</summary>
public enum ToolPayloadClass
{
    /// <summary>Default: tool name, ok/error class, byte count and SHA-256 only.</summary>
    MetadataOnly,

    /// <summary>Exact allowlist: redacted args (≤ 500 chars) and result digest (≤ 1,000 chars) as well.</summary>
    Allowlisted,

    /// <summary>Recall tools: args are the locator; the result is stored as byte count + SHA-256 only.</summary>
    Recall,

    /// <summary><c>mcp__*</c>: the whole payload is dropped (no args, no digest, no hash).</summary>
    Mcp,
}

/// <summary>
/// Persistence policy for tool payloads (§4.1): an EXACT tool-name allowlist, default deny. Every
/// built-in tool must appear in one of the sets below; a test fails when a new tool is added
/// without a review here.
/// </summary>
public static class ToolPayloadPolicy
{
    /// <summary>Maximum persisted argument characters.</summary>
    public const int MaxArgsChars = 500;

    /// <summary>Maximum persisted result-digest characters.</summary>
    public const int MaxDigestChars = 1000;

    private const string McpPrefix = "mcp__";

    private static readonly FrozenSet<string> allowlisted = FrozenSet.ToFrozenSet(
    [
        "date_time", "list_tasks", "schedule_task", "session_timer",
        "todos_read", "todos_write", "sub_agent_start", "sub_agent_read",
        "file_list", "file_find",
    ], StringComparer.Ordinal);

    private static readonly FrozenSet<string> recall = FrozenSet.ToFrozenSet(
    [
        "history_search", "history_read", "history_list_channels", "event_get",
        "memory_search", "memory_get",
    ], StringComparer.Ordinal);

    // Reviewed and deliberately metadata-only: file/page/mail bodies, message text, notes,
    // command output and anything that copies conversation content.
    private static readonly FrozenSet<string> metadataOnly = FrozenSet.ToFrozenSet(
    [
        "file_read", "file_write", "file_edit", "file_delete", "grep", "run_command",
        "send_message", "transfer_session", "revert_transfer",
        "self_notes_read", "self_notes_write", "context_bootstrap_update",
        "memory_ingest", "memory_update", "memory_delete", "todos_delete",
        "sub_agent_send", "sub_agent_stop", "sub_agent_set_goal",
        "coding_folders_list", "coding_session_end", "coding_session_history", "coding_session_interrupt",
        "coding_session_list", "coding_session_respond", "coding_session_resume", "coding_session_send",
        "coding_session_set_goal", "coding_session_start", "coding_session_status",
        "mcp_action_status", "mcp_action_cancel",
        "start_voice_enrollment", "decline_voice_enrollment", "cancel_voice_enrollment",
        "request_voice_reenrollment", "confirm_voice_reenrollment", "forget_voice_enrollment",
    ], StringComparer.Ordinal);

    /// <summary>The class of <paramref name="toolName"/> (exact, case-sensitive match; unknown names are metadata-only).</summary>
    public static ToolPayloadClass Classify(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (toolName.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return ToolPayloadClass.Mcp;
        }

        if (allowlisted.Contains(toolName))
        {
            return ToolPayloadClass.Allowlisted;
        }

        return recall.Contains(toolName) ? ToolPayloadClass.Recall : ToolPayloadClass.MetadataOnly;
    }

    /// <summary>True when <paramref name="toolName"/> was reviewed into one of the policy sets.</summary>
    public static bool IsReviewed(string toolName) =>
        toolName.StartsWith(McpPrefix, StringComparison.Ordinal)
        || allowlisted.Contains(toolName)
        || recall.Contains(toolName)
        || metadataOnly.Contains(toolName);
}
