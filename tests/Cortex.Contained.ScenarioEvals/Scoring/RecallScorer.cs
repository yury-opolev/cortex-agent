using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Scoring;

/// <summary>
/// Purely mechanical scorer: checks if the agent's responses contain expected facts.
/// Case-insensitive substring matching. Score = found / total.
/// </summary>
public sealed class RecallScorer : IScorer
{
    public Task<List<ScoreResult>> ScoreSegmentAsync(
        SegmentDefinition segment,
        IReadOnlyList<Exchange> exchanges,
        MemoryListResult memories,
        Dictionary<string, string[]> facts,
        CancellationToken ct)
    {
        var results = new List<ScoreResult>();
        var scoring = segment.Scoring;
        if (scoring is null)
            return Task.FromResult(results);

        // Score recall_facts
        if (scoring.RecallFacts.Length > 0)
        {
            var allResponses = string.Join("\n", exchanges.Select(e => e.Agent));
            var found = 0;

            var details = new List<string>();
            foreach (var fact in scoring.RecallFacts)
            {
                var recalled = allResponses.Contains(fact, StringComparison.OrdinalIgnoreCase);
                if (recalled) found++;
                details.Add($"  {(recalled ? "[+]" : "[-]")} {fact}");
            }

            var precision = (double)found / scoring.RecallFacts.Length;
            results.Add(new ScoreResult
            {
                Dimension = "recall_precision",
                Label = scoring.Label ?? "recall",
                Value = precision,
                Details = $"Found {found}/{scoring.RecallFacts.Length}:\n{string.Join("\n", details)}"
            });
        }

        // Score soft_facts (informational, does not affect pass/fail)
        if (scoring.SoftFacts.Length > 0)
        {
            var allResponses = string.Join("\n", exchanges.Select(e => e.Agent));
            var found = scoring.SoftFacts.Count(f =>
                allResponses.Contains(f, StringComparison.OrdinalIgnoreCase));

            results.Add(new ScoreResult
            {
                Dimension = "soft_recall",
                Label = scoring.Label ?? "soft_recall",
                Value = (double)found / scoring.SoftFacts.Length,
                Details = $"Found {found}/{scoring.SoftFacts.Length} soft facts"
            });
        }

        // Score response_contains (for task verification)
        if (scoring.ResponseContains.Length > 0)
        {
            var allResponses = string.Join("\n", exchanges.Select(e => e.Agent));
            var found = scoring.ResponseContains.Count(s =>
                allResponses.Contains(s, StringComparison.OrdinalIgnoreCase));

            results.Add(new ScoreResult
            {
                Dimension = "response_contains",
                Label = scoring.Label ?? "response_contains",
                Value = (double)found / scoring.ResponseContains.Length,
                Details = $"Found {found}/{scoring.ResponseContains.Length} expected strings"
            });
        }

        return Task.FromResult(results);
    }
}
