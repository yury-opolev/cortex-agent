using System.Globalization;
using System.Text;
using MemoryMcp.Core.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Frozen;

/// <summary>Raw-vs-sanitised difference of one configuration (§4.2: "report both and the difference").</summary>
public sealed record ConfigDifference(
    string Config,
    int Queries,
    double RawNdcg10,
    double SanitisedNdcg10,
    double DeltaNdcg10,
    double DeltaLow,
    double DeltaHigh,
    double RawRecall10,
    double SanitisedRecall10,
    double RawMrr,
    double SanitisedMrr);

/// <summary>Raw-vs-sanitised result of one paired comparison (e.g. the holdout configuration against the baseline).</summary>
public sealed record ComparisonDifference(string Config, string Baseline, string Slice, double RawMean, double RawLow, double RawHigh, double SanitisedMean, double SanitisedLow, double SanitisedHigh);

/// <summary>
/// Compares a run on the raw local data with the same run on the sanitised frozen copy: per configuration the
/// metrics of both and the paired per-query nDCG@10 difference (sanitised − raw, bootstrap 95% CI over the queries
/// both runs scored), and per paired comparison both confidence intervals, so the owner sees whether sanitising
/// changes the Gate H conclusion. Ids and numbers only.
/// </summary>
public static class SanitisedComparison
{
    /// <summary>Per-configuration differences, by configuration name.</summary>
    public static IReadOnlyList<ConfigDifference> Configs(AblationReport raw, AblationReport sanitised)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(sanitised);
        var result = new List<ConfigDifference>();
        foreach (var config in raw.Summaries.Select(s => s.Config).Intersect(sanitised.Summaries.Select(s => s.Config), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var rawScores = raw.Scores.Where(s => s.Config == config).ToDictionary(s => s.QueryId, StringComparer.Ordinal);
            var paired = sanitised.Scores.Where(s => s.Config == config && rawScores.ContainsKey(s.QueryId)).OrderBy(s => s.QueryId, StringComparer.Ordinal).ToList();
            var sanitisedNdcg = paired.Select(s => s.Ndcg10).ToList();
            var rawNdcg = paired.Select(s => rawScores[s.QueryId].Ndcg10).ToList();
            var (mean, low, high) = EvalStatistics.PairedBootstrap(sanitisedNdcg, rawNdcg);
            var rawSummary = raw.Summaries.Single(s => s.Config == config);
            var sanitisedSummary = sanitised.Summaries.Single(s => s.Config == config);
            result.Add(new ConfigDifference(
                config, paired.Count, rawSummary.Ndcg10, sanitisedSummary.Ndcg10, mean, low, high,
                rawSummary.Recall10, sanitisedSummary.Recall10, rawSummary.Mrr, sanitisedSummary.Mrr));
        }

        return result;
    }

    /// <summary>The paired comparisons present in both runs.</summary>
    public static IReadOnlyList<ComparisonDifference> Comparisons(AblationReport raw, AblationReport sanitised)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(sanitised);
        return raw.Comparisons
            .Join(
                sanitised.Comparisons,
                r => (r.Config, r.Baseline, r.Slice),
                s => (s.Config, s.Baseline, s.Slice),
                (r, s) => new ComparisonDifference(r.Config, r.Baseline, r.Slice, r.Mean, r.Low, r.High, s.Mean, s.Low, s.High))
            .ToList();
    }

    /// <summary>The report.</summary>
    public static string Markdown(string title, IReadOnlyList<ConfigDifference> configs, IReadOnlyList<ComparisonDifference> comparisons)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(comparisons);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# Raw vs sanitised copy — {title}").AppendLine();
        text.AppendLine("| Config | Queries | nDCG@10 raw | nDCG@10 sanitised | Δ (sanitised − raw) [95% CI] | Recall@10 raw / sanitised | MRR raw / sanitised |");
        text.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var c in configs)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Config} | {c.Queries} | {c.RawNdcg10:F3} | {c.SanitisedNdcg10:F3} | {c.DeltaNdcg10:+0.000;-0.000} [{c.DeltaLow:+0.000;-0.000}, {c.DeltaHigh:+0.000;-0.000}] | {c.RawRecall10:F3} / {c.SanitisedRecall10:F3} | {c.RawMrr:F3} / {c.SanitisedMrr:F3} |");
        }

        text.AppendLine().AppendLine("| Comparison | Slice | Raw Δ nDCG@10 [95% CI] | Sanitised Δ nDCG@10 [95% CI] |");
        text.AppendLine("|---|---|---|---|");
        foreach (var c in comparisons)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {c.Config} vs {c.Baseline} | {c.Slice} | {c.RawMean:+0.000;-0.000} [{c.RawLow:+0.000;-0.000}, {c.RawHigh:+0.000;-0.000}] | {c.SanitisedMean:+0.000;-0.000} [{c.SanitisedLow:+0.000;-0.000}, {c.SanitisedHigh:+0.000;-0.000}] |");
        }

        text.AppendLine().AppendLine("Gate decisions use the raw run (D6); the sanitised run shows how far the off-machine copy can stand in for it.");
        return text.ToString();
    }
}
