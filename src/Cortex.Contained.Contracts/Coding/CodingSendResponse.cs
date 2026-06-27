namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Response from <c>SendCodingMessage</c> — the task is in flight; results push back via events.
/// </summary>
public sealed record CodingSendResponse
{
    public required string TaskId { get; init; }

    public required string SessionId { get; init; }

    public required CodingSessionState State { get; init; }

    /// <summary>
    /// True when the message was delivered as a steering comment to a turn already running (the
    /// session was <see cref="CodingSessionState.Working"/>) rather than starting a new turn. The
    /// comment is injected before coda's next model call so the running turn can be redirected.
    /// </summary>
    public bool Steered { get; init; }
}
