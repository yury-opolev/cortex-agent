namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event when coda ended a turn early because it hit a recoverable per-turn limit — the output
/// <c>max_tokens</c> ceiling or the tool-iteration backstop. This is NOT a crash: the session returns
/// to idle and the run can be continued. Distinct from <see cref="CodingErrorEvent"/> (terminal) so
/// the agent can relay it as a soft stop and offer to continue rather than treat it as a failure.
/// </summary>
public sealed record CodingLimitReachedEvent
{
    public required string SessionId { get; init; }

    /// <summary>Stable machine-readable reason, e.g. <c>"max_tokens"</c> or <c>"max_tool_iterations"</c>.</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }
}
