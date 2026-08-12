namespace Cortex.Contained.Contracts.Coding;

/// <summary>Why the Bridge resolved a parked prompt instead of the user.</summary>
public enum CodingPromptResolution
{
    /// <summary>Nobody answered within the timeout; the turn continues with a refusal.</summary>
    Expired = 0,

    /// <summary>The session crashed or was ended while the prompt was still parked.</summary>
    Abandoned = 1,
}

/// <summary>
/// Push event: the Bridge resolved a parked prompt itself, because it went unanswered past the
/// timeout or because the session died while it was parked.
/// <para>
/// Permission and plan prompts are resolved as a refusal, so this is not merely informational —
/// without it the agent sees coda report "permission denied" for something nobody denied, and
/// keeps treating the request id as answerable.
/// </para>
/// </summary>
public sealed record CodingPromptExpiredEvent
{
    public required string SessionId { get; init; }

    /// <summary>The request id that is no longer answerable.</summary>
    public required string RequestId { get; init; }

    public required PendingCodingRequestKind Kind { get; init; }

    /// <summary>
    /// Whether the prompt timed out mid-turn or died with the session. The advice the agent needs
    /// differs: a timeout is worth retrying, a dead session is not.
    /// </summary>
    public required CodingPromptResolution Resolution { get; init; }

    /// <summary>Human-readable explanation of what was auto-resolved and why.</summary>
    public required string Message { get; init; }
}
