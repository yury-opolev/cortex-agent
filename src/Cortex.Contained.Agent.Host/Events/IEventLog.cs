namespace Cortex.Contained.Agent.Host.Events;

/// <summary>
/// The append-only provenance log in <c>messages.db</c>. Appends go through the store's single
/// writer; reads use separate read-only connections.
/// </summary>
public interface IEventLog
{
    /// <summary>
    /// Appends <paramref name="draft"/> in its own writer transaction and returns its ID. With an
    /// <see cref="EventDraft.IdempotencyKey"/> already used, returns the existing event's ID.
    /// </summary>
    Task<string> AppendAsync(EventDraft draft, CancellationToken cancellationToken);

    /// <summary>The event with <paramref name="eventId"/>, or null.</summary>
    Task<EventRecord?> GetAsync(string eventId, CancellationToken cancellationToken);

    /// <summary>Every event of run <paramref name="runId"/>, oldest first.</summary>
    Task<IReadOnlyList<EventRecord>> ListRunAsync(string runId, CancellationToken cancellationToken);

    /// <summary>Every event of send operation <paramref name="operationId"/>, oldest first.</summary>
    Task<IReadOnlyList<EventRecord>> ListOperationAsync(string operationId, CancellationToken cancellationToken);
}
