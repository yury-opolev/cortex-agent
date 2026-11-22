namespace Cortex.Contained.Agent.Host.History;

/// <summary>A fused segment entering the near-duplicate collapse.</summary>
/// <param name="SegId">The segment.</param>
/// <param name="Score">Fused score (higher is better).</param>
/// <param name="AuthorKey">Worker author (e.g. <c>delivered@channel</c>) for segments written by a worker, else null.</param>
/// <param name="Vector">Unit vector of the segment's best chunk, or null when it has none.</param>
public sealed record CollapseCandidate(long SegId, double Score, string? AuthorKey, float[]? Vector);

/// <summary>
/// Collapses near-duplicate worker segments (§4.2), e.g. daily scheduled-task briefs that read almost
/// the same: greedy maximal marginal relevance where only segments of the same worker author penalise
/// each other, and a same-author segment at least <c>duplicateSimilarity</c> close to one already
/// chosen is dropped. Segments without an author key keep their fused order.
/// </summary>
public static class NearDuplicateCollapse
{
    /// <summary>Returns the new order and how many segments were collapsed away.</summary>
    public static (IReadOnlyList<long> Order, int Collapsed) Apply(IReadOnlyList<CollapseCandidate> ranked, double lambda, double duplicateSimilarity)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        var maxScore = ranked.Count == 0 ? 1 : Math.Max(ranked.Max(c => c.Score), double.Epsilon);
        var remaining = ranked.ToList();
        var chosen = new List<CollapseCandidate>();
        var collapsed = 0;
        while (remaining.Count > 0)
        {
            CollapseCandidate? best = null;
            var bestValue = double.NegativeInfinity;
            foreach (var candidate in remaining.ToList())
            {
                var similarity = MaxSameAuthorSimilarity(candidate, chosen);
                if (similarity >= duplicateSimilarity)
                {
                    remaining.Remove(candidate);
                    collapsed++;
                    continue;
                }

                var value = (lambda * candidate.Score / maxScore) - ((1 - lambda) * similarity);
                if (value > bestValue)
                {
                    best = candidate;
                    bestValue = value;
                }
            }

            if (best is null)
            {
                break;
            }

            chosen.Add(best);
            remaining.Remove(best);
        }

        return (chosen.Select(c => c.SegId).ToList(), collapsed);
    }

    private static double MaxSameAuthorSimilarity(CollapseCandidate candidate, List<CollapseCandidate> chosen)
    {
        if (candidate.AuthorKey is null || candidate.Vector is null)
        {
            return 0;
        }

        var max = 0.0;
        foreach (var other in chosen)
        {
            if (other.Vector is not null && string.Equals(other.AuthorKey, candidate.AuthorKey, StringComparison.Ordinal))
            {
                max = Math.Max(max, VectorMath.Dot(candidate.Vector, other.Vector));
            }
        }

        return max;
    }
}
