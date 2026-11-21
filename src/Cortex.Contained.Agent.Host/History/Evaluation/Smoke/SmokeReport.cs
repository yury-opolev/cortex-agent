using System.Globalization;
using System.Text;

namespace Cortex.Contained.Agent.Host.History.Evaluation.Smoke;

/// <summary>One query × system × budget score (IDs and numbers only — never message text).</summary>
public sealed record SmokeScoreRow(string QueryId, string System, int Budget, bool Locator, bool Excerpt, int? LinesToFirstGold, int WordsRead);

/// <summary>First-page latency of one system for one query.</summary>
public sealed record SmokeLatencyRow(string QueryId, string System, double FirstPageMs);

/// <summary>The §14.2 gate: (b) beats (c) on excerpt recall at 1,500 words by a sign test.</summary>
public sealed record SmokeGateResult(int Wins, int Losses, int Ties, double PValue, bool Passed, IReadOnlyList<string> LossQueryIds);

/// <summary>Aggregation and the Markdown report of a smoke run (§14.2).</summary>
public static class SmokeReport
{
    /// <summary>System (a): <c>history_search</c> with the owner's oracle query.</summary>
    public const string Oracle = "a-oracle";

    /// <summary>System (b): <c>history_search</c> with the main model's query.</summary>
    public const string Model = "b-model";

    /// <summary>System (c): term-wise <c>LIKE</c> baseline with the model's query terms.</summary>
    public const string Like = "c-like";

    /// <summary>Reading budgets in words.</summary>
    public static IReadOnlyList<int> Budgets { get; } = [500, 1500, 3000];

    /// <summary>Computes the gate from excerpt recall at 1,500 words.</summary>
    public static SmokeGateResult Gate(IReadOnlyList<SmokeScoreRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var model = rows.Where(r => r.System == Model && r.Budget == 1500).ToDictionary(r => r.QueryId, r => r.Excerpt, StringComparer.Ordinal);
        var like = rows.Where(r => r.System == Like && r.Budget == 1500).ToDictionary(r => r.QueryId, r => r.Excerpt, StringComparer.Ordinal);
        int wins = 0, losses = 0, ties = 0;
        var lossIds = new List<string>();
        foreach (var (id, modelHit) in model.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var likeHit = like.GetValueOrDefault(id);
            if (modelHit == likeHit)
            {
                ties++;
            }
            else if (modelHit)
            {
                wins++;
            }
            else
            {
                losses++;
                lossIds.Add(id);
            }
        }

        var p = EvalStatistics.SignTestTwoSided(wins, losses);
        return new SmokeGateResult(wins, losses, ties, p, wins > losses && p < 0.05, lossIds);
    }

    /// <summary>The run report (no message text).</summary>
    public static string Markdown(
        IReadOnlyList<SmokeScoreRow> rows,
        IReadOnlyList<SmokeLatencyRow> latency,
        IReadOnlyList<(int Messages, int Words)> segments)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(latency);
        ArgumentNullException.ThrowIfNull(segments);
        var report = new StringBuilder();
        report.AppendLine("# History locator smoke evaluation");
        report.AppendLine();
        report.AppendLine("| System | Budget (words) | Locator recall | Excerpt recall (95% Wilson CI) | Median lines to first gold |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var system in new[] { Oracle, Model, Like })
        {
            foreach (var budget in Budgets)
            {
                var cell = rows.Where(r => r.System == system && r.Budget == budget).ToList();
                if (cell.Count == 0)
                {
                    continue;
                }

                var excerpt = cell.Count(r => r.Excerpt);
                var (low, high) = EvalStatistics.WilsonInterval(excerpt, cell.Count);
                var costs = cell.Where(r => r.LinesToFirstGold is not null).Select(r => (double)r.LinesToFirstGold!.Value).ToList();
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {system} | {budget} | {cell.Count(r => r.Locator)}/{cell.Count} | {excerpt}/{cell.Count} ({low:P0}–{high:P0}) | {(costs.Count == 0 ? "–" : EvalStatistics.Percentile(costs, 50).ToString("F0", CultureInfo.InvariantCulture))} |");
            }
        }

        report.AppendLine();
        report.AppendLine("| System | p50 latency (ms) | p95 latency (ms) |");
        report.AppendLine("|---|---|---|");
        foreach (var group in latency.GroupBy(l => l.System).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var values = group.Select(l => l.FirstPageMs).ToList();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {EvalStatistics.Percentile(values, 50):F1} | {EvalStatistics.Percentile(values, 95):F1} |");
        }

        report.AppendLine();
        report.AppendLine("## Segment sizes");
        report.AppendLine();
        report.AppendLine("| Words | Segments |");
        report.AppendLine("|---|---|");
        foreach (var (label, low, high) in new[] { ("1–49", 1, 49), ("50–199", 50, 199), ("200–499", 200, 499), ("500–999", 500, 999), ("1000+", 1000, int.MaxValue) })
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {segments.Count(s => s.Words >= low && s.Words <= high)} |");
        }

        var gate = Gate(rows);
        report.AppendLine();
        report.AppendLine("## Gate: (b) vs (c), excerpt recall at 1,500 words");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"wins {gate.Wins}, losses {gate.Losses}, ties {gate.Ties}, two-sided sign test p = {gate.PValue:F4} → **{(gate.Passed ? "PASS" : "FAIL")}**");
        report.AppendLine();
        report.AppendLine("Every loss must be explained and categorised (inflection, language mismatch, tokenisation, coverage) in the local analysis:");
        report.AppendLine();
        report.AppendLine("| Query | Category | Explanation |");
        report.AppendLine("|---|---|---|");
        foreach (var id in gate.LossQueryIds)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"| {id} | | |");
        }

        return report.ToString();
    }
}
