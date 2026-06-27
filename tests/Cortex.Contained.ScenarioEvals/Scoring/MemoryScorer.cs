using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Scoring;

/// <summary>
/// Checks that persona facts have been stored in the agent's memory system.
/// Uses substring matching against memory content retrieved via the Bridge API.
/// </summary>
public sealed class MemoryScorer : IScorer
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
        if (scoring is null || scoring.MemoryFacts.Length == 0)
            return Task.FromResult(results);

        var allMemoryContent = string.Join("\n",
            memories.Items.Select(m => $"{m.Title} {m.Content}"));

        var found = 0;
        var details = new List<string>();

        foreach (var fact in scoring.MemoryFacts)
        {
            var stored = allMemoryContent.Contains(fact, StringComparison.OrdinalIgnoreCase);
            if (stored) found++;
            details.Add($"  {(stored ? "[+]" : "[-]")} {fact}");
        }

        var precision = (double)found / scoring.MemoryFacts.Length;
        results.Add(new ScoreResult
        {
            Dimension = "memory_recall",
            Label = scoring.Label ?? "memory_check",
            Value = precision,
            Details = $"Found {found}/{scoring.MemoryFacts.Length} facts in memory:\n{string.Join("\n", details)}"
        });

        // Also report total memory count
        results.Add(new ScoreResult
        {
            Dimension = "memory_count",
            Label = "memory_count",
            Value = memories.TotalCount
        });

        return Task.FromResult(results);
    }
}
