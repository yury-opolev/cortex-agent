using System.Globalization;
using System.Text;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Reranking.Evaluation;

namespace Cortex.Contained.Agent.Host.Evaluation.Orchestration;

/// <summary>The facts an automated analysis plan states (counts, models, κ; never text).</summary>
public sealed record PlanFacts
{
    public required string Tool { get; init; }

    public required DateTimeOffset Date { get; init; }

    public required int Queries { get; init; }

    public required int Dev { get; init; }

    public required IReadOnlyDictionary<string, int> Categories { get; init; }

    public required IReadOnlyDictionary<string, int> Strata { get; init; }

    public required string QueryModel { get; init; }

    public required string JudgeModel { get; init; }

    /// <summary>The judge rubric's id (<see cref="Labelling.RelevanceJudge.RubricId"/>).</summary>
    public string JudgeRubric { get; init; } = "default";

    public required int PoolDepth { get; init; }

    public required double Kappa { get; init; }

    public double? ToolKappa { get; init; }

    public required int KappaItems { get; init; }

    public required string Baseline { get; init; }

    public required double Margin { get; init; }

    public required bool Reranker { get; init; }
}

/// <summary>
/// The pre-registered analysis plan of an automated run (§4.2), written before the first dev ablation and
/// registered with the dataset and <c>ablation.json</c> before the held-out run. It fixes the configuration selection
/// rule (<see cref="ConfigSelector"/>, <see cref="RerankCellSelector"/>) so the choice made on the dev split is
/// mechanical.
/// </summary>
public static class AnalysisPlanWriter
{
    /// <summary>The plan's Markdown.</summary>
    public static string Render(PlanFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var memory = facts.Tool == "memory-eval";
        var unit = memory ? "memory" : "message";
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"# {(memory ? "Hybrid memory search" : "Hybrid history search")} — pre-registered analysis plan (automated run)");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"Written by the evaluation run on {facts.Date:yyyy-MM-dd} before the first dev ablation. `register` fixes this file, `dataset.jsonl` and `ablation.json` before the held-out run.");
        text.AppendLine();
        text.AppendLine("## Data");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Snapshot: {facts.Date:yyyy-MM-dd}. A result on a mostly pre-event-log snapshot is provisional (re-run after ≥ 4 weeks of live capture).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Dataset: {facts.Queries} queries, {facts.Dev} dev / {facts.Queries - facts.Dev} test (`EvalSplits.Assign`, seed 20261121, 30 % dev). Categories: {Counts(facts.Categories)}. Strata: {Counts(facts.Strata)} (`real` = real recall requests, co-primary).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Queries written by `{facts.QueryModel}` from sampled snippets; graded 0–2 per {unit} by `{facts.JudgeModel}` (rubric `{facts.JudgeRubric}`) on the union of the top {facts.PoolDepth} of every configuration in `ablation.json` plus the source (the model families differ).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- LLM-vs-human κ = {facts.Kappa:F3} on {facts.KappaItems} blind hand-graded items of both datasets{(facts.ToolKappa is { } k ? string.Create(CultureInfo.InvariantCulture, $" (this dataset: {k:F3})") : string.Empty)}; LLM labels are used only with κ ≥ 0.6.");
        text.AppendLine("- Paraphrase guard (Jaccard ≤ 0.2, no shared rare token): enforced while drafting and again against the judged gold.");
        text.AppendLine("- Finalisation (before any comparison): a synthetic query is dropped when the judge found its source irrelevant (every source graded 0 — other relevant candidates do not save it); a real request with no relevant item becomes an abstention query; an abstention query with a relevant item, an enumeration/multi query with fewer than two relevant items, a paraphrase failing the guard and tag_filtered without tags become `lookup`.");
        text.AppendLine();
        text.AppendLine("## Hypotheses and metrics");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Primary: nDCG@10 of `holdout.config` vs `{facts.Baseline}` on the test split; non-inferiority margin {facts.Margin.ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine("- Co-primary: the same on the `stratum:real` slice.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Secondary: recall@10, MRR, words to the first relevant {unit}, judged@10, {(memory ? "multi" : "enumeration")} set F1, abstention Brier/ECE, p95 latency; oracle vs model-written queries (`*-model-queries`).");
        text.AppendLine("- Raw data and the sanitised frozen copy: both reported with their difference; the gate uses the raw run.");
        text.AppendLine();
        text.AppendLine("## Configuration selection (dev split only)");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Candidates: every configuration in `ablation.json` except `{facts.Baseline}`, the `*-model-queries` configurations and reranked ones, with dev judged@10 ≥ {ConfigSelector.MinJudged.ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Chosen: the highest lower bound of the paired 95 % CI of Δ nDCG@10 vs `{facts.Baseline}` on dev; ties by the higher mean, then by name. It becomes `holdout.config` with margin {facts.Margin.ToString(CultureInfo.InvariantCulture)}.");
        text.AppendLine(facts.Reranker
            ? "- Gate R: the rerank curve on dev (normal load, the chosen configuration as base); the cell with the highest CI lower bound among cells with p95 ≤ 2.5 s and a lower bound > 0 is added as `<config>-rerank-<cell>` and tested by `gate-r` on test; no such cell, no Gate R."
            : "- Gate R: not part of this run (the reranker was off).");
        text.AppendLine();
        text.AppendLine("## Statistics");
        text.AppendLine("- Paired bootstrap 95 % CI (10,000 resamples, seed 20261121) and a paired randomisation test.");
        text.AppendLine("- Per-category results as CIs only; dev ablations are exploratory; only the single pre-registered holdout comparison is confirmatory.");
        text.AppendLine();
        text.AppendLine("## Decision rule (owner)");
        text.AppendLine(memory
            ? "- Gate H for memory passes when the primary comparison is non-inferior and no category's CI shows a loss the owner will not accept; only then switch `MemoryMcp:Search:DefaultMode`."
            : "- Gate H passes when the primary comparison is non-inferior and the behaviour gate thresholds hold.");
        return text.ToString();
    }

    private static string Counts(IReadOnlyDictionary<string, int> counts) =>
        counts.Count == 0 ? "none" : string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} {c.Value}")));
}

/// <summary>The pre-declared choice of <c>holdout.config</c> from the dev ablation (see the analysis plan).</summary>
public static class ConfigSelector
{
    /// <summary>Configurations with less dev judged@10 are not chosen (the pool would not cover them).</summary>
    public const double MinJudged = 0.8;

    /// <summary>The chosen configuration among <paramref name="candidates"/>, or null when none qualifies.</summary>
    public static string? Select(AblationReport dev, string baseline, IReadOnlyCollection<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(candidates);
        var judged = dev.Summaries.ToDictionary(s => s.Config, s => s.Judged10, StringComparer.Ordinal);
        return dev.Comparisons
            .Where(c => c.Slice == "all" && c.Baseline == baseline && c.Config != baseline && candidates.Contains(c.Config)
                && judged.GetValueOrDefault(c.Config) >= MinJudged)
            .OrderByDescending(c => c.Low)
            .ThenByDescending(c => c.Mean)
            .ThenBy(c => c.Config, StringComparer.Ordinal)
            .Select(c => c.Config)
            .FirstOrDefault();
    }
}

/// <summary>The pre-declared choice of the reranked configuration for Gate R from the dev rerank curve.</summary>
public static class RerankCellSelector
{
    /// <summary>The chosen budget, or null when no cell is fast enough with a CI lower bound above zero.</summary>
    public static RerankBudget? Select(IReadOnlyList<RerankCurveRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var cell = rows
            .Where(r => r.P95EndToEndMs <= RerankEvaluation.MaxP95Ms && r.Low > 0)
            .OrderByDescending(r => r.Low)
            .ThenByDescending(r => r.DeltaNdcg)
            .ThenBy(r => r.Cell, StringComparer.Ordinal)
            .FirstOrDefault();
        return cell is null ? null : RerankGrid.Cells().First(c => c.Label == cell.Cell);
    }
}
