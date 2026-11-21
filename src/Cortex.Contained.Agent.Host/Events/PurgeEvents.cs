using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>The content-free tombstone written in the purge transaction (§4.1 purge generation).</summary>
public static class PurgeEvents
{
    /// <summary>The <c>history_purged</c> event for one purge.</summary>
    public static EventDraft Tombstone(HistoryPurgeScope scope, int deletedMessages, int deletedEvents, long purgeGeneration, int? replacementRows, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var payload = new JsonObject
        {
            ["deletedMessages"] = deletedMessages,
            ["deletedEvents"] = deletedEvents,
            ["purgeGeneration"] = purgeGeneration,
        };
        if (scope.ChannelId is not null)
        {
            payload["channelId"] = scope.ChannelId;
        }

        if (scope.OlderThan is { } olderThan)
        {
            payload["olderThan"] = SqliteDateTimeText.Format(olderThan);
        }

        if (replacementRows is { } rows)
        {
            payload["replacementRows"] = rows;
        }

        return new EventDraft(EventTypes.HistoryPurged, AuthorKinds.System)
        {
            ChannelId = scope.ChannelId,
            Ts = now,
            Payload = payload,
        };
    }
}
