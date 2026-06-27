namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Summary of a channel's message history: how many messages the channel has and when
/// the most recent one was written. Used by per-channel history management endpoints.
/// </summary>
public sealed record ChannelSummary
{
    /// <summary>Channel identifier (e.g. "webchat-default", "discord-voice-default").</summary>
    public required string ChannelId { get; init; }

    /// <summary>Total number of messages stored for this channel (all categories).</summary>
    public required int MessageCount { get; init; }

    /// <summary>Timestamp of the most recent message in this channel.</summary>
    public required DateTimeOffset LastActivity { get; init; }
}
