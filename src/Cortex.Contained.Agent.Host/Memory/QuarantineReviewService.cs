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

    /// <summary>Page size when the caller does not choose one.</summary>
    public const int DefaultPageSize = 200;

    /// <summary>Largest page of the review list (each entry carries the memory's full content).</summary>
    public const int MaxPageSize = 500;

    private readonly MemoryQuarantine quarantine;
    private readonly IMemoryManagementService memories;

    public QuarantineReviewService(MemoryQuarantine quarantine, IMemoryManagementService memories)
    {
        this.quarantine = quarantine;
        this.memories = memories;
    }

    /// <summary>Quarantined memories, newest first: one page with content and the purge that quarantined each.</summary>
    /// <param name="offset">Entries to skip.</param>
    /// <param name="limit">Page size, at most <see cref="MaxPageSize"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<QuarantineReviewListDto> ListAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            throw new ArgumentException("offset must not be negative.", nameof(offset));
        }

        if (limit is < 1 or > MaxPageSize)
        {
            throw new ArgumentException($"limit must be between 1 and {MaxPageSize}.", nameof(limit));
        }

        var entries = await this.quarantine.ListAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<QuarantinedMemoryDto>();
        foreach (var entry in entries.Skip(offset).Take(limit))
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

        return new QuarantineReviewListDto { Items = items, Total = entries.Count, Offset = offset, Limit = limit };
    }

    /// <summary>Every quarantined memory id in list order (no content), so the page can select all of them.</summary>
    public async Task<IReadOnlyList<string>> ListIdsAsync(CancellationToken cancellationToken) =>
        (await this.quarantine.ListAsync(cancellationToken).ConfigureAwait(false)).Select(e => e.MemoryId).ToList();

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
        return await this.quarantine.WithDecisionLockAsync(
            async ct =>
            {
                var quarantined = await this.quarantine.GetQuarantinedIdsAsync(ct).ConfigureAwait(false);
                var requested = memoryIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
                var delete = requested.Where(quarantined.Contains).ToList();
                var skipped = requested.Where(id => !quarantined.Contains(id)).ToList();
                var deleted = delete.Count == 0 ? 0 : await this.memories.DeleteManyAsync(delete, ct).ConfigureAwait(false);
                return new QuarantineDecisionResultDto { Affected = deleted, Skipped = skipped };
            },
            cancellationToken).ConfigureAwait(false);
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
