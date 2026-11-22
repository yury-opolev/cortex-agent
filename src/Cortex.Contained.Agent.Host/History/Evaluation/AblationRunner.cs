namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>What one system returned for one query.</summary>
/// <param name="Hits">The hits in reading order.</param>
/// <param name="Confidence">Confidence that an answer exists (the best dense similarity; 0 for keyword-only).</param>
/// <param name="LatencyMs">First-page latency in milliseconds.</param>
public sealed record RetrievalRun(IReadOnlyList<ReturnedHit> Hits, double Confidence, double LatencyMs)
{
    /// <summary>The rerank stage's outcome on the first page, or null when the configuration does not rerank.</summary>
    public Reranking.RerankOutcome? Rerank { get; init; }
}

/// <summary>
/// Runs every configuration on every query, scores message-level metrics and compares each
/// configuration with the baseline using paired bootstrap CIs and a randomisation test (§4.2,
/// the shared <see cref="AblationReporting"/>).
/// </summary>
public static class AblationRunner
{
    /// <summary>Scores <paramref name="configs"/> × <paramref name="queries"/>.</summary>
    public static async Task<AblationReport> RunAsync(
        IReadOnlyList<AblationConfig> configs,
        IReadOnlyList<EvalQuery> queries,
        Func<AblationConfig, EvalQuery, CancellationToken, Task<RetrievalRun>> search,
        string baseline,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(search);
        var scores = new List<AblationScore>();
        foreach (var config in configs)
        {
            foreach (var query in queries)
            {
                var run = await search(config, query, cancellationToken).ConfigureAwait(false);
                scores.Add(Score(config.Name, query, run));
            }
        }

        return AblationReporting.Build(scores, baseline);
    }

    /// <summary>Metrics of one run.</summary>
    public static AblationScore Score(string config, EvalQuery query, RetrievalRun run)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(run);
        var ranked = RankingMetrics.RankedMessages(run.Hits);
        var grades = query.Judgments.GroupBy(j => j.MessageId).ToDictionary(g => g.Key, g => g.Max(j => j.Grade));
        var relevant = query.Relevant;
        return new AblationScore(
            config,
            query.Id,
            query.Category,
            query.Stratum,
            RankingMetrics.NdcgAt(ranked, grades, 10),
            RankingMetrics.RecallAt(ranked, relevant, 10),
            RankingMetrics.ReciprocalRank(ranked, relevant),
            RankingMetrics.JudgedAt(ranked, grades.Keys.ToHashSet(), 10),
            RankingMetrics.WordsToFirstRelevant(run.Hits, relevant),
            query.Category == "enumeration" ? RankingMetrics.SetF1(ranked, relevant) : null,
            run.Confidence,
            query.ExpectAbstain,
            run.LatencyMs);
    }

    /// <summary>A Markdown report: summaries and comparisons (numbers only).</summary>
    public static string Markdown(AblationReport report, string title) => AblationReporting.Markdown(report, title);
}
