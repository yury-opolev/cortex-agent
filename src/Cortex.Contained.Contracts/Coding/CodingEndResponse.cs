namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Response from <c>EndCodingSession</c> / <c>InterruptCodingSession</c>.
/// </summary>
public sealed record CodingEndResponse
{
    public required string SessionId { get; init; }

    public required CodingSessionState State { get; init; }

    public string? InterruptedTaskId { get; init; }
}
