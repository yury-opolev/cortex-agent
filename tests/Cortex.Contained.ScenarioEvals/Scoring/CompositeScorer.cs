using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Abstractions;
using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Scoring;

/// <summary>
/// Combines all scorer dimensions by delegating to individual scorers.
/// Runs all applicable scorers and merges their results.
/// </summary>
public sealed class CompositeScorer : IScorer
{
    private readonly IReadOnlyList<IScorer> _scorers;

    public CompositeScorer(IReadOnlyList<IScorer> scorers)
    {
        _scorers = scorers;
    }

    public async Task<List<ScoreResult>> ScoreSegmentAsync(
        SegmentDefinition segment,
        IReadOnlyList<Exchange> exchanges,
        MemoryListResult memories,
        Dictionary<string, string[]> facts,
        CancellationToken ct)
    {
        var allResults = new List<ScoreResult>();

        foreach (var scorer in _scorers)
        {
            var results = await scorer.ScoreSegmentAsync(segment, exchanges, memories, facts, ct);
            allResults.AddRange(results);
        }

        return allResults;
    }
}
