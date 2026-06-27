using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Abstractions;

/// <summary>
/// Scores agent performance on a segment based on exchanges, memories, and expected facts.
/// </summary>
public interface IScorer
{
    /// <summary>
    /// Score a segment's exchanges against its scoring criteria.
    /// </summary>
    /// <param name="segment">The segment definition with scoring criteria.</param>
    /// <param name="exchanges">The recorded exchanges for this segment.</param>
    /// <param name="memories">Current memory state from the agent.</param>
    /// <param name="facts">The persona's known facts by category.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scores for each applicable dimension.</returns>
    Task<List<ScoreResult>> ScoreSegmentAsync(
        SegmentDefinition segment,
        IReadOnlyList<Exchange> exchanges,
        MemoryListResult memories,
        Dictionary<string, string[]> facts,
        CancellationToken ct);
}
