namespace Cortex.Contained.Agent.Host.Events;

/// <summary>Event types of the <c>Events</c> table (spec §3).</summary>
public static class EventTypes
{
    /// <summary>A user message was stored (<c>Messages</c> row, role user, category Normal).</summary>
    public const string MessageReceived = "message_received";

    /// <summary>An assistant message was stored (a reply or a proactive delivery).</summary>
    public const string MessageSent = "message_sent";

    /// <summary>A system or transfer row was stored.</summary>
    public const string SystemNote = "system_note";

    /// <summary>An internal row was stored (task instruction, worker summary, subagent completion).</summary>
    public const string InternalNote = "internal_note";

    /// <summary>A stored message's content was replaced (barge-in truncation); supersedes the original.</summary>
    public const string MessageCorrected = "message_corrected";

    /// <summary>A stored message's tool-call summary was patched.</summary>
    public const string MessageUpdated = "message_updated";

    /// <summary>A row without native provenance: tenant import or the explicit backfill.</summary>
    public const string LegacyImport = "legacy_import";

    /// <summary>A tool was called.</summary>
    public const string ToolCall = "tool_call";

    /// <summary>A tool returned.</summary>
    public const string ToolResult = "tool_result";

    /// <summary>A send to a channel was attempted.</summary>
    public const string DeliveryAttempted = "delivery_attempted";

    /// <summary>The Bridge answered a send (delivered or rejected).</summary>
    public const string DeliveryAcknowledged = "delivery_acknowledged";

    /// <summary>A send failed in transport; whether it arrived is unknown.</summary>
    public const string DeliveryUnknown = "delivery_unknown";

    /// <summary>Tombstone of a purge (content-free).</summary>
    public const string HistoryPurged = "history_purged";
}

/// <summary>Who produced an event.</summary>
public static class AuthorKinds
{
    /// <summary>The user.</summary>
    public const string User = "user";

    /// <summary>The main agent in a live conversation turn.</summary>
    public const string Main = "main";

    /// <summary>The agent running a scheduled task in its own session.</summary>
    public const string ScheduledTask = "scheduled_task";

    /// <summary>The agent running a fired session timer.</summary>
    public const string Timer = "timer";

    /// <summary>A subagent run.</summary>
    public const string Subagent = "subagent";

    /// <summary>The runtime itself (system rows, purges).</summary>
    public const string System = "system";

    /// <summary>Imported or backfilled rows without native provenance.</summary>
    public const string Legacy = "legacy";
}
