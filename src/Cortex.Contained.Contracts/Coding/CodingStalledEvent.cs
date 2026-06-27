namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event when the idle watchdog detects coda has gone unresponsive mid-turn — a stall, not a
/// logic error. Distinct from <see cref="CodingErrorEvent"/> so the agent can relay it as
/// <c>status=stalled</c> (resumable) and avoid churn rather than treat the task as a hard failure.
/// </summary>
public sealed record CodingStalledEvent
{
    public required string SessionId { get; init; }

    /// <summary>Seconds of no activity that elapsed before the watchdog declared the stall.</summary>
    public int IdleSeconds { get; init; }

    /// <summary>True if coda was mid-LLM-stream when it went silent.</summary>
    public bool WasStreaming { get; init; }

    /// <summary>Chars streamed for the in-flight LLM call before the stall, if any.</summary>
    public long? StreamedChars { get; init; }

    public string? StderrTail { get; init; }

    public required string Message { get; init; }
}
