namespace Cortex.Contained.Contracts.Recording;

/// <summary>
/// Public snapshot of an active recording session — what external consumers
/// (slash-command handler, status pages) see. The full internal session
/// (writers, gates) lives inside the Bridge implementation; this DTO is the
/// boundary type that crosses the Contracts seam.
/// </summary>
public sealed record ActiveSession(
    string Id,
    string ChannelKey,
    string Label,
    DateTimeOffset StartUtc,
    long CapMs);
