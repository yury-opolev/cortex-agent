using System.Text.RegularExpressions;

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
/// Fuses the keyword (BM25) and dense lists (§4.2): reciprocal rank fusion with one <c>k</c>, weighted
/// RRF, or a convex combination of min-max normalised scores. Queries with exact intent (quoted
/// phrases, identifier-like tokens) multiply the keyword weight, giving the BM25 list priority.
/// Ties prefer the better keyword rank, then the lower segment id, so results are deterministic.
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

        var keywordWeight = options.Fusion == HistoryFusionMethod.Rrf ? 1.0 : options.KeywordWeight;
        var denseWeight = options.Fusion == HistoryFusionMethod.Rrf ? 1.0 : options.DenseWeight;
        if (exactIntent)
        {
            keywordWeight *= options.ExactIntentKeywordBoost;
        }

        var keywordRanks = Ranks(keyword);
        var denseRanks = Ranks(dense);
        var keywordNorm = options.Fusion == HistoryFusionMethod.Convex ? Normalised(keyword) : null;
        var denseNorm = options.Fusion == HistoryFusionMethod.Convex ? Normalised(dense) : null;
        var total = keywordWeight + denseWeight;

        return keywordRanks.Keys.Union(denseRanks.Keys)
            .Select(segId =>
            {
                int? kr = keywordRanks.TryGetValue(segId, out var k) ? k : null;
                int? dr = denseRanks.TryGetValue(segId, out var d) ? d : null;
                double score;
                if (options.Fusion == HistoryFusionMethod.Convex)
                {
                    score = ((keywordWeight * keywordNorm!.GetValueOrDefault(segId)) + (denseWeight * denseNorm!.GetValueOrDefault(segId))) / total;
                }
                else
                {
                    score = (kr is { } a ? keywordWeight / (options.RrfK + a) : 0) + (dr is { } b ? denseWeight / (options.RrfK + b) : 0);
                }

                return new FusedCandidate(segId, score, kr, dr);
            })
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.KeywordRank ?? int.MaxValue)
            .ThenBy(c => c.SegId)
            .ToList();
    }

    private static Dictionary<long, int> Ranks(IReadOnlyList<RankedCandidate> list)
    {
        var ranks = new Dictionary<long, int>();
        foreach (var candidate in list)
        {
            ranks.TryAdd(candidate.SegId, ranks.Count + 1);
        }

        return ranks;
    }

    private static Dictionary<long, double> Normalised(IReadOnlyList<RankedCandidate> list)
    {
        var scores = new Dictionary<long, double>();
        if (list.Count == 0)
        {
            return scores;
        }

        var min = list.Min(c => c.Score);
        var max = list.Max(c => c.Score);
        foreach (var candidate in list)
        {
            scores.TryAdd(candidate.SegId, max > min ? (candidate.Score - min) / (max - min) : 1.0);
        }

        return scores;
    }
}

/// <summary>Detects queries that ask for exact wording or identifiers (BM25 priority, §4.2).</summary>
public static partial class QueryIntent
{
    /// <summary>
    /// True for a quoted phrase or an identifier-like token: letters mixed with digits (<c>INV-2041</c>,
    /// <c>v1.2</c>), an inner underscore (<c>foo_bar</c>), camelCase (<c>getUser</c>) or <c>#123</c>.
    /// Plain hyphenated words are not identifiers.
    /// </summary>
    public static bool HasExactIntent(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        if (query.Count(c => c == '"') >= 2)
        {
            return true;
        }

        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('*', ',', '.', '!', '?', ';', ':', '(', ')'))
            .Any(t => IdentifierLike().IsMatch(t));
    }

    [GeneratedRegex(@"^(?=.*\p{L})(?=.*\d)[\p{L}\d._\-/#@]+$|^[\p{L}\d]+(_[\p{L}\d]+)+$|\p{Ll}\p{Lu}|^#\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierLike();
}
