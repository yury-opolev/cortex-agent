namespace Cortex.Contained.ScenarioEvals.Model;

/// <summary>Result of a single user↔agent exchange.</summary>
public sealed class Exchange
{
    public required string User { get; init; }
    public required string Agent { get; init; }
    public TokenUsageInfo? AgentTokens { get; init; }
}

/// <summary>Token usage from a single LLM call.</summary>
public sealed class TokenUsageInfo
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}

/// <summary>Score for a single evaluation dimension.</summary>
public sealed class ScoreResult
{
    public required string Dimension { get; init; }
    public string? Label { get; init; }
    public required double Value { get; init; }
    public string? Details { get; init; }
}

/// <summary>Aggregate result of scoring a segment or phase.</summary>
public sealed class SegmentScoreResult
{
    public List<ScoreResult> Scores { get; init; } = [];
}

/// <summary>Result of running a single phase.</summary>
public sealed class PhaseResult
{
    public required string PhaseName { get; init; }
    public long DurationMs { get; init; }
    public List<Exchange> Exchanges { get; init; } = [];
    public List<MemorySnapshot> MemoriesAfter { get; init; } = [];
    public List<ScoreResult> Scores { get; init; } = [];
}

/// <summary>Snapshot of a single memory for recording.</summary>
public sealed class MemorySnapshot
{
    public required string MemoryId { get; init; }
    public string? Title { get; init; }
    public required string Content { get; init; }
    public string[] Tags { get; init; } = [];
}

/// <summary>Full result of running one scenario.</summary>
public sealed class ScenarioResult
{
    public required string ScenarioId { get; init; }
    public bool Passed { get; init; }
    public long DurationMs { get; init; }
    public int TotalExchanges { get; init; }
    public int FinalMemoryCount { get; init; }
    public List<PhaseResult> Phases { get; init; } = [];
    public List<TokenUsageSummary> TokenUsage { get; init; } = [];
}

/// <summary>Aggregated token usage per role.</summary>
public sealed class TokenUsageSummary
{
    public required string ScenarioId { get; init; }
    public required string PhaseName { get; init; }
    public required string Role { get; init; }
    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public int TokensTotal { get; init; }
}

/// <summary>Full transcript entry for human-readable output.</summary>
public sealed class TranscriptEntry
{
    public required string Type { get; init; }
    public string? Phase { get; init; }
    public string? Segment { get; init; }
    public string? ActorPrompt { get; init; }
    public string? UserMessage { get; init; }
    public string? AgentResponse { get; init; }
    public string? Event { get; init; }
    public List<MemorySnapshot>? Memories { get; init; }
    public List<ScoreResult>? Scores { get; init; }
}
