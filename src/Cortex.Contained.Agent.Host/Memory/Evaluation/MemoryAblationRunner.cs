namespace Cortex.Contained.Agent.Host.Memory.Evaluation;

/// <summary>One returned memory and its word count (reading cost).</summary>
public sealed record ReturnedMemory(string MemoryId, int Words);

/// <summary>What one configuration returned for one query.</summary>
/// <param name="Memories">Returned memories, best first.</param>
/// <param name="Confidence">Confidence that an answer exists (best semantic similarity; 0 for keyword-only).</param>
/// <param name="LatencyMs">Search latency in milliseconds.</param>
public sealed record MemoryRetrievalRun(IReadOnlyList<ReturnedMemory> Memories, double Confidence, double LatencyMs);

/// <summary>
/// Runs every configuration on every query, scores memory-level metrics with the shared
/// <see cref="RetrievalMetrics"/> and compares each configuration with the baseline using the shared
/// paired bootstrap / randomisation reporting (<see cref="AblationReporting"/>).
/// </summary>
public static class MemoryAblationRunner
{
    /// <summary>Scores <paramref name="configs"/> × <paramref name="queries"/>.</summary>
    public static async Task<AblationReport> RunAsync(
        IReadOnlyList<MemoryAblationConfig> configs,
        IReadOnlyList<MemoryEvalQuery> queries,
        Func<MemoryAblationConfig, MemoryEvalQuery, CancellationToken, Task<MemoryRetrievalRun>> search,
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

    /// <summary>Metrics of one run: nDCG@10, recall@10, MRR, judged@10, words to the first relevant memory, set F1 for <c>multi</c>.</summary>
    public static AblationScore Score(string config, MemoryEvalQuery query, MemoryRetrievalRun run)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(run);
        var ranked = run.Memories.Select(m => m.MemoryId).Distinct(StringComparer.Ordinal).ToList();
        var grades = query.Judgments.GroupBy(j => j.MemoryId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Max(j => j.Grade), StringComparer.Ordinal);
        var relevant = query.Relevant;
        return new AblationScore(
            config,
            query.Id,
            query.Category,
            query.Stratum,
            RetrievalMetrics.NdcgAt(ranked, grades, 10),
            RetrievalMetrics.RecallAt(ranked, relevant, 10),
            RetrievalMetrics.ReciprocalRank(ranked, relevant),
            RetrievalMetrics.JudgedAt(ranked, grades.Keys.ToHashSet(StringComparer.Ordinal), 10),
            RetrievalMetrics.WordsToFirstRelevant(run.Memories.Select(m => (m.MemoryId, m.Words)), relevant),
            query.Category == "multi" ? RetrievalMetrics.SetF1(ranked, relevant) : null,
            run.Confidence,
            query.ExpectAbstain,
            run.LatencyMs);
    }
}
