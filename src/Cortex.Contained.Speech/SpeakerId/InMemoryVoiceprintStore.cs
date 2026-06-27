namespace Cortex.Contained.Speech.SpeakerId;

using System.Collections.Concurrent;

/// <summary>
/// In-memory <see cref="IVoiceprintStore"/> for development and tests.
/// Not persistent. Production wiring uses the SQLite-backed implementation
/// in <c>Cortex.Contained.Agent.Host</c> (Phase 2).
/// </summary>
public sealed class InMemoryVoiceprintStore : IVoiceprintStore
{
    private readonly ConcurrentDictionary<string, VoiceprintRecord> records = new(StringComparer.Ordinal);

    public ValueTask<VoiceprintRecord?> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.records.TryGetValue(tenantId, out var record);
        return ValueTask.FromResult(record);
    }

    public ValueTask UpsertAsync(VoiceprintRecord record, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.records[record.TenantId] = record;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.records.TryRemove(tenantId, out _);
        return ValueTask.CompletedTask;
    }
}
