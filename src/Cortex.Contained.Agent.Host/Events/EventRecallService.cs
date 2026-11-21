using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Events;

/// <summary>Result of <see cref="EventRecallService.GetAsync"/>.</summary>
public sealed record EventRecallResult
{
    /// <summary>The event, or null with <see cref="Error"/>.</summary>
    public EventRecord? Event { get; init; }

    /// <summary>The current source row of a message event (read now), or null.</summary>
    public MessageRecord? Message { get; init; }

    /// <summary>True when the event describes a message row that no longer exists.</summary>
    public bool MessageDeleted { get; init; }

    /// <summary>The causing event and the linked evidence, as far as they still exist.</summary>
    public IReadOnlyList<EventRecord> Linked { get; init; } = [];

    /// <summary>Why nothing is returned.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Executes <c>event_get</c> under the recall contract: one read lease (linearizable with purges)
/// and one snapshot for the event, the source row it describes and its links. Content always comes
/// from the authoritative <c>Messages</c> row, never from a copy.
/// </summary>
public sealed class EventRecallService
{
    /// <summary>Most linked events returned.</summary>
    internal const int MaxLinked = 10;

    private readonly IHistorySource source;
    private readonly HistoryAccessGate gate;

    public EventRecallService(IHistorySource source, HistoryAccessGate gate)
    {
        this.source = source;
        this.gate = gate;
    }

    /// <summary>Reads event <paramref name="eventId"/> with its source row and links.</summary>
    public async Task<EventRecallResult> GetAsync(string eventId, bool includeInternal, CancellationToken cancellationToken)
    {
        await using var lease = await this.gate.AcquireReadAsync(cancellationToken).ConfigureAwait(false);
        await using var snapshot = await this.source.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var record = await snapshot.ReadEventAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return new EventRecallResult { Error = $"Event {eventId} was not found. It may have been deleted." };
        }

        MessageRecord? row = null;
        if (record.MessageRowId is { } rowId)
        {
            row = await snapshot.ReadByIdAsync(rowId, cancellationToken).ConfigureAwait(false);
            if (row?.Category == MessageCategory.Internal && !includeInternal)
            {
                return new EventRecallResult { Error = $"Event {eventId} describes an internal message (not necessarily delivered). Pass includeInternal=true to read it." };
            }
        }

        var linkIds = new List<string>();
        if (record.CausationId is { } causation)
        {
            linkIds.Add(causation);
        }

        linkIds.AddRange((record.Payload["evidence"]?.AsArray() ?? []).Select(n => (string?)n).OfType<string>());
        var linked = new List<EventRecord>();
        foreach (var id in linkIds.Distinct(StringComparer.Ordinal).Take(MaxLinked))
        {
            if (await snapshot.ReadEventAsync(id, cancellationToken).ConfigureAwait(false) is { } link)
            {
                linked.Add(link);
            }
        }

        return new EventRecallResult
        {
            Event = record,
            Message = row,
            MessageDeleted = record.MessageRowId is not null && row is null,
            Linked = linked,
        };
    }
}
