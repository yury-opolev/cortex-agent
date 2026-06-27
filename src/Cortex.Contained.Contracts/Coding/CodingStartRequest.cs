namespace Cortex.Contained.Contracts.Coding;

/// <summary>
/// Request to start a new external-agent session pinned to a working folder.
/// </summary>
public sealed record CodingStartRequest
{
    public required string ChannelId { get; init; }

    public required string WorkingFolder { get; init; }

    /// <summary>
    /// Legacy back-compat flag.  Prefer <see cref="RequestedPolicy"/> for new callers.
    /// When both are set, <see cref="RequestedPolicy"/> takes precedence.
    /// </summary>
    public bool Yolo { get; init; }

    /// <summary>
    /// Explicit policy requested by the caller.  <c>null</c> means "use the folder's
    /// configured ceiling as the default".  When present it must not exceed the ceiling.
    /// </summary>
    public CodingPolicy? RequestedPolicy { get; init; }

    public string? SessionName { get; init; }

    public string? Goal { get; init; }

    public bool SessionMemory { get; init; }
}
