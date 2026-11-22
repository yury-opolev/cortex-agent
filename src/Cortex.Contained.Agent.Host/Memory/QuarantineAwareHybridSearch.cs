using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// <see cref="IHybridMemorySearch"/> decorator that hides quarantined memories (D10) from
/// <c>memory_search</c>: their ids join the request's excluded ids, which the store applies
/// <b>before</b> scoring in both the keyword and the semantic list, so they never take a result slot.
/// </summary>
public sealed class QuarantineAwareHybridSearch : IHybridMemorySearch
{
    private readonly IHybridMemorySearch inner;
    private readonly MemoryQuarantine quarantine;

    public QuarantineAwareHybridSearch(IHybridMemorySearch inner, MemoryQuarantine quarantine)
    {
        this.inner = inner;
        this.quarantine = quarantine;
    }

    /// <inheritdoc />
    public async Task<HybridSearchResult> SearchAsync(HybridSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hidden = await this.quarantine.GetQuarantinedIdsAsync(cancellationToken).ConfigureAwait(false);
        if (hidden.Count == 0)
        {
            return await this.inner.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var excluded = new HashSet<string>(hidden, StringComparer.Ordinal);
        if (request.Filter.ExcludeIds is { } existing)
        {
            excluded.UnionWith(existing);
        }

        var filtered = request with { Filter = request.Filter with { ExcludeIds = excluded } };
        return await this.inner.SearchAsync(filtered, cancellationToken).ConfigureAwait(false);
    }
}
