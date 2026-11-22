namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>
/// Retrieval metrics of the history evaluation protocol (§4.2) over message-level graded labels:
/// the shared <see cref="RetrievalMetrics"/> on message ids, plus the reading order of returned hits
/// (<see cref="RankedMessages"/>) and the locator cost in words read.
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
    public static double NdcgAt(IReadOnlyList<long> ranked, IReadOnlyDictionary<long, int> grades, int k) =>
        RetrievalMetrics.NdcgAt(ranked, grades, k);

    /// <summary>Share of relevant messages found in the first <paramref name="k"/>; 0 when nothing is relevant.</summary>
    public static double RecallAt(IReadOnlyList<long> ranked, IReadOnlySet<long> relevant, int k) =>
        RetrievalMetrics.RecallAt(ranked, relevant, k);

    /// <summary>Reciprocal rank of the first relevant message (0 when none is returned).</summary>
    public static double ReciprocalRank(IReadOnlyList<long> ranked, IReadOnlySet<long> relevant) =>
        RetrievalMetrics.ReciprocalRank(ranked, relevant);

    /// <summary>Share of the first <paramref name="k"/> returned messages that carry any judgment (pool depth check).</summary>
    public static double JudgedAt(IReadOnlyList<long> ranked, IReadOnlySet<long> judged, int k) =>
        RetrievalMetrics.JudgedAt(ranked, judged, k);

    /// <summary>F1 between the returned and the relevant sets (enumeration queries); 1 when both are empty.</summary>
    public static double SetF1(IReadOnlyCollection<long> returned, IReadOnlySet<long> relevant) =>
        RetrievalMetrics.SetF1(returned, relevant);

    /// <summary>Words read, in order, up to and including the first relevant line; null when none is returned.</summary>
    public static int? WordsToFirstRelevant(IReadOnlyList<ReturnedHit> hits, IReadOnlySet<long> relevant)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return RetrievalMetrics.WordsToFirstRelevant(hits.SelectMany(h => h.Lines).Select(l => (l.MessageId, l.Words)), relevant);
    }

    /// <summary>Calibration of a confidence that an answer exists against whether one does (Brier, ECE).</summary>
    public static CalibrationScore Calibration(IReadOnlyList<(double Confidence, bool AnswerExists)> points) =>
        RetrievalMetrics.Calibration(points);
}
