using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// <see cref="IMemoryService"/> decorator that hides quarantined memories (D10) from similarity
/// search. It over-fetches by the number of quarantined memories so callers still get up to
/// <c>limit</c> visible results. Used by subagent seeding, consolidation and compaction (their
/// candidate sets are unchanged); <c>memory_search</c> uses <see cref="QuarantineAwareHybridSearch"/>,
/// which excludes the ids before scoring, and the memory UI keeps using the undecorated service so the
/// owner can review.
/// </summary>
public sealed class QuarantineAwareMemoryService : IMemoryService
{
    private readonly IMemoryService inner;
    private readonly MemoryQuarantine quarantine;

    public QuarantineAwareMemoryService(IMemoryService inner, MemoryQuarantine quarantine)
    {
        this.inner = inner;
        this.quarantine = quarantine;
    }

    /// <inheritdoc />
    public Task<IngestResult> IngestAsync(string content, string? title = null, List<string>? tags = null, bool force = false, CancellationToken cancellationToken = default) =>
        this.inner.IngestAsync(content, title, tags, force, cancellationToken);

    /// <inheritdoc />
    public Task<MemoryResult?> GetAsync(string memoryId, CancellationToken cancellationToken = default) =>
        this.inner.GetAsync(memoryId, cancellationToken);

    /// <inheritdoc />
    public Task<MemoryResult?> UpdateAsync(string memoryId, string? content = null, string? title = null, List<string>? tags = null, CancellationToken cancellationToken = default) =>
        this.inner.UpdateAsync(memoryId, content, title, tags, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string memoryId, CancellationToken cancellationToken = default) =>
        this.inner.DeleteAsync(memoryId, cancellationToken);

    /// <inheritdoc />
    public Task<List<SearchResult>> SearchAsync(string query, int limit = 5, float? minScore = null, List<string>? tags = null, CancellationToken cancellationToken = default) =>
        this.FilteredAsync(limit, fetch => this.inner.SearchAsync(query, fetch, minScore, tags, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<List<SearchResult>> FindSimilarAsync(string text, int limit, float minScore, CancellationToken cancellationToken = default) =>
        this.FilteredAsync(limit, fetch => this.inner.FindSimilarAsync(text, fetch, minScore, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public Task<List<SearchResult>> FindSimilarAsync(float[] vector, int limit, float minScore, CancellationToken cancellationToken = default) =>
        this.FilteredAsync(limit, fetch => this.inner.FindSimilarAsync(vector, fetch, minScore, cancellationToken), cancellationToken);

    private async Task<List<SearchResult>> FilteredAsync(int limit, Func<int, Task<List<SearchResult>>> search, CancellationToken cancellationToken)
    {
        var hidden = await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false);
        if (hidden.Count == 0)
        {
            return await search(limit).ConfigureAwait(false);
        }

        var results = await search(limit + hidden.Count).ConfigureAwait(false);
        return results.Where(r => !hidden.Contains(r.MemoryId)).Take(limit).ToList();
    }
}
