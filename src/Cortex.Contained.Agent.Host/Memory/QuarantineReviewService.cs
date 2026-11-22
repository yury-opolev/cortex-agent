using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// The owner's review of quarantined memories (D10) on the Recall page: the list with the purge that
/// quarantined each memory, and bulk keep / bulk delete. Only memories that are quarantined right now are
/// ever kept or deleted, so a stale or forged id list cannot touch other memories. Bulk delete is one
/// <see cref="IMemoryManagementService.DeleteManyAsync"/> call (a single keyword-index rewrite).
/// </summary>
public sealed class QuarantineReviewService
{
    /// <summary>Largest id list one keep or delete request may carry.</summary>
    public const int MaxBatch = 1000;

    private readonly MemoryQuarantine quarantine;
    private readonly IMemoryManagementService memories;

    public QuarantineReviewService(MemoryQuarantine quarantine, IMemoryManagementService memories)
    {
        this.quarantine = quarantine;
        this.memories = memories;
    }

    /// <summary>Quarantined memories, newest first, with content and the purge that quarantined them.</summary>
    public async Task<QuarantineReviewListDto> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<QuarantinedMemoryDto>();
        foreach (var entry in await this.quarantine.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await this.memories.GetAsync(entry.MemoryId, cancellationToken).ConfigureAwait(false) is not { } memory)
            {
                continue;
            }

            items.Add(new QuarantinedMemoryDto
            {
                Memory = memory,
                PurgeId = entry.Source.ErasureId,
                PurgedAt = entry.Source.PurgedAt,
                PurgeChannelId = entry.Source.ChannelId,
                WindowFrom = entry.Source.Window.From,
                WindowTo = entry.Source.Window.To,
            });
        }

        return new QuarantineReviewListDto { Items = items };
    }

    /// <summary>Keeps the quarantined members of <paramref name="memoryIds"/>.</summary>
    public async Task<QuarantineDecisionResultDto> KeepAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        CheckBatch(memoryIds);
        var (kept, skipped) = await this.quarantine.KeepManyAsync(memoryIds, cancellationToken).ConfigureAwait(false);
        return new QuarantineDecisionResultDto { Affected = kept.Count, Skipped = skipped };
    }

    /// <summary>Erases the quarantined members of <paramref name="memoryIds"/> in one batch.</summary>
    public async Task<QuarantineDecisionResultDto> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken cancellationToken)
    {
        CheckBatch(memoryIds);
        var quarantined = await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false);
        var requested = memoryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        var delete = requested.Where(quarantined.Contains).ToList();
        var skipped = requested.Where(id => !quarantined.Contains(id)).ToList();
        var deleted = delete.Count == 0 ? 0 : await this.memories.DeleteManyAsync(delete, cancellationToken).ConfigureAwait(false);
        return new QuarantineDecisionResultDto { Affected = deleted, Skipped = skipped };
    }

    private static void CheckBatch(IReadOnlyList<string> memoryIds)
    {
        ArgumentNullException.ThrowIfNull(memoryIds);
        if (memoryIds.Count > MaxBatch)
        {
            throw new ArgumentException($"At most {MaxBatch} memories per request.", nameof(memoryIds));
        }
    }
}
