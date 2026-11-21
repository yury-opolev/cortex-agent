namespace Cortex.Contained.Agent.Host.History;

/// <summary>One entry of a ranked list (keyword or dense), best first.</summary>
/// <param name="SegId">The segment.</param>
/// <param name="Score">The list's own score (BM25 relevance or cosine similarity); higher is better.</param>
public sealed record RankedCandidate(long SegId, double Score);

/// <summary>A segment after fusion.</summary>
/// <param name="KeywordRank">1-based rank in the keyword list, or null.</param>
/// <param name="DenseRank">1-based rank in the dense list, or null.</param>
public sealed record FusedCandidate(long SegId, double Score, int? KeywordRank, int? DenseRank);

/// <summary>
/// Fuses the keyword (BM25) and dense segment lists (§4.2) with the shared engine
/// (<see cref="RankFusion"/>): reciprocal rank fusion with one <c>k</c>, weighted RRF, or a convex
/// combination of min-max normalised scores; exact intent gives the BM25 list priority. Ties prefer
/// the better keyword rank, then the lower segment id, so results are deterministic.
/// </summary>
public static class HybridFusion
{
    /// <summary>Fuses <paramref name="keyword"/> and <paramref name="dense"/>.</summary>
    public static IReadOnlyList<FusedCandidate> Fuse(
        IReadOnlyList<RankedCandidate> keyword,
        IReadOnlyList<RankedCandidate> dense,
        HistoryRankingOptions options,
        bool exactIntent)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentNullException.ThrowIfNull(dense);
        ArgumentNullException.ThrowIfNull(options);

        return RankFusion.Fuse(
                keyword.Select(c => new RankedItem<long>(c.SegId, c.Score)).ToList(),
                dense.Select(c => new RankedItem<long>(c.SegId, c.Score)).ToList(),
                options.ToFusionSettings(),
                exactIntent,
                Comparer<long>.Default)
            .Select(f => new FusedCandidate(f.Key, f.Score, f.KeywordRank, f.DenseRank))
            .ToList();
    }
}
