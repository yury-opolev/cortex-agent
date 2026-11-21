using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// The event written in the same transaction as each <c>Messages</c> mutation. Payloads describe
/// the row (role, category, length, SHA-256) and never copy its content: evidence views read the
/// current row, so a correction or a purge leaves no stale copy behind.
/// </summary>
public static class MessageEvents
{
    /// <summary>The event type for a stored row.</summary>
    public static string TypeFor(string role, MessageCategory category) => category switch
    {
        MessageCategory.Internal => EventTypes.InternalNote,
        MessageCategory.System or MessageCategory.Transfer => EventTypes.SystemNote,
        _ when string.Equals(role, "user", StringComparison.Ordinal) => EventTypes.MessageReceived,
        _ when string.Equals(role, "assistant", StringComparison.Ordinal) => EventTypes.MessageSent,
        _ => EventTypes.SystemNote,
    };

    /// <summary>The event for a row stored live by <paramref name="run"/> (or outside any run).</summary>
    public static EventDraft ForInsert(MessageRecord row, RunProvenance? run)
    {
        ArgumentNullException.ThrowIfNull(row);
        var type = TypeFor(row.Role, row.Category);
        var authorKind = type switch
        {
            EventTypes.MessageReceived => AuthorKinds.User,
            EventTypes.SystemNote => AuthorKinds.System,
            _ => run?.AuthorKind ?? AuthorKinds.Main,
        };

        return new EventDraft(type, authorKind)
        {
            AuthorId = run is not null && authorKind == run.AuthorKind ? run.AuthorId : null,
            ChannelId = row.ChannelId,
            RunId = run?.RunId,
            MessageRowId = row.Id,
            Ts = row.Timestamp,
            Payload = Describe(row.Role, row.Category, row.Content),
        };
    }

    /// <summary>The event for an imported row (tenant import, append import, explicit backfill).</summary>
    public static EventDraft ForImport(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new EventDraft(EventTypes.LegacyImport, AuthorKinds.Legacy)
        {
            ChannelId = row.ChannelId,
            MessageRowId = row.Id,
            Ts = row.Timestamp,
            IdempotencyKey = LegacyKey(row.Id),
            Payload = Describe(row.Role, row.Category, row.Content),
        };
    }

    /// <summary>The event for an in-place content or tool-call change of row <paramref name="rowId"/>.</summary>
    /// <param name="column"><c>Content</c> (a correction that supersedes the original) or <c>ToolCalls</c>.</param>
    public static EventDraft ForUpdate(string column, long rowId, string channelId, string? value, string? causationId, RunProvenance? run)
    {
        var correction = string.Equals(column, "Content", StringComparison.Ordinal);
        var payload = new JsonObject { ["field"] = correction ? "content" : "toolCalls" };
        if (value is not null)
        {
            payload["chars"] = value.Length;
            payload["sha256"] = EventHashing.Sha256Hex(value);
        }

        return new EventDraft(correction ? EventTypes.MessageCorrected : EventTypes.MessageUpdated, run?.AuthorKind ?? AuthorKinds.Main)
        {
            AuthorId = run?.AuthorId,
            ChannelId = channelId,
            RunId = run?.RunId,
            CausationId = causationId,
            MessageRowId = rowId,
            Payload = payload,
        };
    }

    /// <summary>Idempotency key of the legacy event of row <paramref name="rowId"/>.</summary>
    public static string LegacyKey(long rowId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"legacy:{rowId}");

    private static JsonObject Describe(string role, MessageCategory category, string content) => new()
    {
        ["role"] = role,
        ["category"] = (int)category,
        ["chars"] = content.Length,
        ["sha256"] = EventHashing.Sha256Hex(content),
    };
}
