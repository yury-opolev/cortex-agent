using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>Quarantine windows and owner decisions stored in <c>messages.db</c> (D10).</summary>
public interface IMemoryQuarantineSource
{
    /// <summary>Every recorded quarantine window.</summary>
    Task<IReadOnlyList<QuarantineWindow>> ReadQuarantineWindowsAsync(CancellationToken cancellationToken);

    /// <summary>Memories the owner chose to keep after review.</summary>
    Task<IReadOnlySet<string>> ReadKeptMemoryIdsAsync(CancellationToken cancellationToken);

    /// <summary>Records that the owner kept <paramref name="memoryId"/>.</summary>
    Task RecordKeptMemoryAsync(string memoryId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>Creation time of one curated memory.</summary>
public sealed record MemoryCreationTime(string MemoryId, DateTimeOffset CreatedAt);

/// <summary>Lists the creation time of every curated memory.</summary>
public interface IMemoryCreationTimes
{
    /// <summary>All memories with their <c>CreatedAt</c>.</summary>
    Task<IReadOnlyList<MemoryCreationTime>> ListCreationTimesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// D10: curated memories whose <c>CreatedAt</c> falls in a
/// purged time window are quarantined — hidden from <c>memory_search</c>, subagent seeding,
/// consolidation and compaction until the owner keeps or deletes them. Never auto-deleted.
/// Time-window quarantine remains the mechanism; recording which events a memory was extracted
/// from (event provenance for memories) is a possible future refinement.
/// </summary>
public sealed class MemoryQuarantine
{
    private readonly IMemoryQuarantineSource source;
    private readonly IMemoryCreationTimes memories;
    private readonly TimeProvider time;

    public MemoryQuarantine(IMemoryQuarantineSource source, IMemoryCreationTimes memories, TimeProvider time)
    {
        this.source = source;
        this.memories = memories;
        this.time = time;
    }

    /// <summary>Ids of memories currently quarantined.</summary>
    public async Task<IReadOnlySet<string>> GetQuarantinedIdsAsync(CancellationToken cancellationToken)
    {
        var windows = await this.source.ReadQuarantineWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (windows.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await this.InWindowsAsync(windows, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many not-kept memories fall in <paramref name="windows"/> (for the delete confirmation).</summary>
    public async Task<int> CountInWindowsAsync(IReadOnlyList<QuarantineWindow> windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Count == 0 ? 0 : (await this.InWindowsAsync(windows, cancellationToken).ConfigureAwait(false)).Count;
    }

    /// <summary>The owner keeps <paramref name="memoryId"/>: it leaves quarantine for good.</summary>
    public Task KeepAsync(string memoryId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryId);
        return this.source.RecordKeptMemoryAsync(memoryId, this.time.GetUtcNow(), cancellationToken);
    }

    private async Task<IReadOnlySet<string>> InWindowsAsync(IReadOnlyList<QuarantineWindow> windows, CancellationToken cancellationToken)
    {
        var kept = await this.source.ReadKeptMemoryIdsAsync(cancellationToken).ConfigureAwait(false);
        var all = await this.memories.ListCreationTimesAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(m => !kept.Contains(m.MemoryId) && windows.Any(w => w.Contains(m.CreatedAt)))
            .Select(m => m.MemoryId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
