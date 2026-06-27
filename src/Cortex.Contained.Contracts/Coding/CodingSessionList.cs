namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Wrapper for the list of session statuses, to keep the SignalR contract symmetric.
/// </summary>
public sealed record CodingSessionList
{
    public required IReadOnlyList<CodingStatus> Sessions { get; init; }
}
