namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Request to send a user message to a running session. Non-blocking; returns a task ID.
/// </summary>
public sealed record CodingSendRequest
{
    public required string SessionId { get; init; }

    public required string Message { get; init; }
}
