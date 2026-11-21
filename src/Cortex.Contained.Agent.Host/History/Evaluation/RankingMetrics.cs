namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>Abstention calibration summary.</summary>
/// <param name="Brier">Mean squared error of the confidence that an answer exists.</param>
/// <param name="ExpectedCalibrationError">Bin-weighted |accuracy − confidence| over ten equal-width bins.</param>
public sealed record CalibrationScore(double Brier, double ExpectedCalibrationError);

/// <summary>
/// Retrieval metrics of the evaluation protocol (§4.2) over message-level graded labels: nDCG@k
/// (gain 2^grade − 1), recall@k, MRR, judged@k, set F1 for enumeration queries, the locator cost
/// (words read until the first relevant line) and abstention calibration.
/// </summary>
public static class RankingMetrics
{
    /// <summary>Distinct message ids in the order a reader meets them in <paramref name="hits"/>.</summary>
    public static IReadOnlyList<long> RankedMessages(IReadOnlyList<ReturnedHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        var seen = new HashSet<long>();
        return hits.SelectMany(h => h.Lines).Select(l => l.MessageId).Where(seen.Add).ToList();
    }

    /// <summary>Normalised discounted cumulative gain at <paramref name="k"/>; 0 when nothing is relevant.</summary>
    public static double NdcgAt(IReadOnlyList<long> ranked, IReadOnlyDictionary<long, int> grades, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(grades);
        static double Gain(int grade, int position) => (Math.Pow(2, grade) - 1) / Math.Log2(position + 2);

        var dcg = ranked.Take(k).Select((id, i) => Gain(grades.GetValueOrDefault(id), i)).Sum();
        var ideal = grades.Values.Where(g => g > 0).OrderDescending().Take(k).Select((g, i) => Gain(g, i)).Sum();
        return ideal == 0 ? 0 : dcg / ideal;
    }

    /// <summary>Share of relevant messages found in the first <paramref name="k"/>; 0 when nothing is relevant.</summary>
    public static double RecallAt(IReadOnlyList<long> ranked, IReadOnlySet<long> relevant, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(relevant);
        return relevant.Count == 0 ? 0 : (double)ranked.Take(k).Count(relevant.Contains) / relevant.Count;
    }

    /// <summary>Reciprocal rank of the first relevant message (0 when none is returned).</summary>
    public static double ReciprocalRank(IReadOnlyList<long> ranked, IReadOnlySet<long> relevant)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(relevant);
        for (var i = 0; i < ranked.Count; i++)
        {
            if (relevant.Contains(ranked[i]))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0;
    }

    /// <summary>Share of the first <paramref name="k"/> returned messages that carry any judgment (pool depth check).</summary>
    public static double JudgedAt(IReadOnlyList<long> ranked, IReadOnlySet<long> judged, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(judged);
        var top = ranked.Take(k).ToList();
        return top.Count == 0 ? 1 : (double)top.Count(judged.Contains) / top.Count;
    }

    /// <summary>F1 between the returned and the relevant sets (enumeration queries); 1 when both are empty.</summary>
    public static double SetF1(IReadOnlyCollection<long> returned, IReadOnlySet<long> relevant)
    {
        ArgumentNullException.ThrowIfNull(returned);
        ArgumentNullException.ThrowIfNull(relevant);
        if (returned.Count == 0 && relevant.Count == 0)
        {
            return 1;
        }

        var hits = returned.Distinct().Count(relevant.Contains);
        return hits == 0 ? 0 : 2.0 * hits / (returned.Distinct().Count() + relevant.Count);
    }

    /// <summary>Words read, in order, up to and including the first relevant line; null when none is returned.</summary>
    public static int? WordsToFirstRelevant(IReadOnlyList<ReturnedHit> hits, IReadOnlySet<long> relevant)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(relevant);
        var words = 0;
        foreach (var line in hits.SelectMany(h => h.Lines))
        {
            words += line.Words;
            if (relevant.Contains(line.MessageId))
            {
                return words;
            }
        }

        return null;
    }

    /// <summary>
    /// Calibration of a confidence that an answer exists (e.g. the best dense similarity) against
    /// whether one does: Brier score and expected calibration error over ten equal-width bins.
    /// </summary>
    public static CalibrationScore Calibration(IReadOnlyList<(double Confidence, bool AnswerExists)> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return new CalibrationScore(0, 0);
        }

        var clamped = points.Select(p => (Confidence: Math.Clamp(p.Confidence, 0, 1), Outcome: p.AnswerExists ? 1.0 : 0.0)).ToList();
        var brier = clamped.Average(p => Math.Pow(p.Confidence - p.Outcome, 2));
        var ece = clamped
            .GroupBy(p => Math.Min(9, (int)(p.Confidence * 10)))
            .Sum(bin => (double)bin.Count() / clamped.Count * Math.Abs(bin.Average(p => p.Outcome) - bin.Average(p => p.Confidence)));
        return new CalibrationScore(brier, ece);
    }
}
