using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// The rerank stage for curated-memory search (§4.3): applied after hybrid fusion only, to the first
/// <see cref="RerankBudget.TopN"/> fused memories, each given as its title and content lines (raw text,
/// best-matching lines kept first within the budget). Callers ask the search for
/// <see cref="CandidateLimit"/> memories and get <c>limit</c> back. Shared by <c>memory_search</c> and
/// the memory evaluation.
/// </summary>
public static class HybridMemoryReranking
{
    /// <summary>How many memories to request so the reranker sees its whole budget.</summary>
    public static int CandidateLimit(int limit, RerankBudget? budget) => budget is null ? limit : Math.Max(limit, budget.TopN);

    /// <summary>The raw lines of one memory: its title, then its non-empty content lines.</summary>
    public static IReadOnlyList<string> Lines(SearchResult memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(memory.Title))
        {
            lines.Add(memory.Title);
        }

        lines.AddRange(memory.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return lines;
    }

    /// <summary>
    /// Reorders the head of a hybrid <paramref name="result"/> and cuts it to <paramref name="limit"/>.
    /// Results of other modes (including a fallback) are only cut, with a "not used" note.
    /// </summary>
    public static async Task<(HybridSearchResult Result, RerankOutcome? Outcome, string Note)> ApplyAsync(
        IRerankStage? stage, string query, HybridSearchResult result, int limit, RerankBudget budget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(budget);
        if (result.EffectiveMode != MemorySearchMode.Hybrid)
        {
            return (result with { Hits = result.Hits.Take(limit).ToList() }, null,
                $"not used: mode={result.EffectiveMode.ToString().ToLowerInvariant()} (reranking follows hybrid fusion)");
        }

        if (stage is null)
        {
            return (result with { Hits = result.Hits.Take(limit).ToList() }, null, "skipped: no reranker is configured");
        }

        var window = Math.Min(budget.TopN, result.Hits.Count);
        var candidates = result.Hits.Take(window).Select(h => Lines(h.Memory)).ToList();
        var outcome = await stage.RerankAsync(query, candidates, budget, cancellationToken).ConfigureAwait(false);
        var hits = outcome.Order.Select(i => result.Hits[i]).Concat(result.Hits.Skip(window)).Take(limit).ToList();
        return (result with { Hits = hits }, outcome, outcome.Note);
    }
}
