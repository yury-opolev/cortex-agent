using Cortex.Contained.ScenarioEvals.Model;

namespace Cortex.Contained.ScenarioEvals.Abstractions;

/// <summary>
/// Persists eval run results to SQLite for A/B comparison across runs.
/// </summary>
public interface IResultStore : IAsyncDisposable
{
    /// <summary>Create a new run record and return its ID.</summary>
    Task<long> CreateRunAsync(string label, string? gitCommit, string? agentModel, string? evalModel);

    /// <summary>Record a completed scenario result.</summary>
    Task<long> RecordScenarioAsync(long runId, ScenarioResult result);

    /// <summary>Record a phase result within a scenario.</summary>
    Task<long> RecordPhaseAsync(long scenarioResultId, PhaseResult phase);

    /// <summary>Record scores for a phase.</summary>
    Task RecordScoresAsync(long phaseResultId, IReadOnlyList<ScoreResult> scores);

    /// <summary>Record token usage for a phase.</summary>
    Task RecordTokenUsageAsync(long runId, TokenUsageSummary usage);

    /// <summary>Mark a run as finished.</summary>
    Task FinishRunAsync(long runId);
}
