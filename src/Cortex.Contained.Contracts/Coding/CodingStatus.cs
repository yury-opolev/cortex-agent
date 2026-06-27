namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Snapshot of an external-agent session, returned by the status and list tools.
/// </summary>
public sealed record CodingStatus
{
    public required string SessionId { get; init; }

    public required string ChannelId { get; init; }

    public required string WorkingFolder { get; init; }

    public required CodingSessionState State { get; init; }

    public required CodingPolicy Policy { get; init; }

    public string? SessionName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastActivityAt { get; init; }

    public string? CurrentTaskId { get; init; }

    public string? LastUserMessage { get; init; }

    public string? LastAssistantSummary { get; init; }

    public IReadOnlyList<CodingToolCall> LastToolCalls { get; init; } = [];

    /// <summary>Path to the per-run telemetry log coda reported at <c>initialize</c>, if any.</summary>
    public string? TelemetryLogPath { get; init; }

    /// <summary>The most recent error message raised for this session, if any.</summary>
    public string? LastError { get; init; }

    /// <summary>Latest cumulative input-token count streamed by coda via <c>event/usage</c>.</summary>
    public long? InputTokens { get; init; }

    /// <summary>Latest cumulative output-token count streamed by coda via <c>event/usage</c>.</summary>
    public long? OutputTokens { get; init; }

    /// <summary>True while coda is actively streaming an LLM response right now.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Chars streamed so far for the in-flight LLM call, if any pulse has arrived.</summary>
    public long? StreamedChars { get; init; }

    /// <summary>Chunks streamed so far for the in-flight LLM call, if any pulse has arrived.</summary>
    public long? StreamedChunks { get; init; }

    /// <summary>UTC time of the last LLM stream pulse, if any — how cortex sees coda is "still working".</summary>
    public DateTimeOffset? LastStreamActivityAt { get; init; }

    /// <summary>Human-readable "what coda is doing right now", if known (e.g. streaming response).</summary>
    public string? CurrentActivity { get; init; }

    /// <summary>
    /// Latest autonomous-goal run status (outcome, continuations, elapsed, what remains), or null
    /// if the session has no goal / no goal run has completed yet.
    /// </summary>
    public CodingGoalStatus? GoalStatus { get; init; }
}
