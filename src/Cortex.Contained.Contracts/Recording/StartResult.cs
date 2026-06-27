namespace Cortex.Contained.Contracts.Recording;

/// <summary>
/// Outcome of <see cref="IRecordingController.StartAsync"/>. The discriminated
/// subtypes carry exactly the information the slash-command handler needs to
/// build a reply.
/// </summary>
public abstract record StartResult
{
    public sealed record Started(string Id, string ChannelKey, DateTimeOffset StartUtc, long CapMs)
        : StartResult;

    public sealed record AlreadyActive(string ExistingId, DateTimeOffset SinceUtc)
        : StartResult;

    public sealed record Rejected(string Reason)
        : StartResult;
}
