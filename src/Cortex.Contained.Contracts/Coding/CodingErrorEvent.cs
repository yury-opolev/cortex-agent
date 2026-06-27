namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Push event when the session crashes or hits a hard error.
/// </summary>
public sealed record CodingErrorEvent
{
    public required string SessionId { get; init; }

    public int? ExitCode { get; init; }

    public string? StderrTail { get; init; }

    public required string Message { get; init; }
}
