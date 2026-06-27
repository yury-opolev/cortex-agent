namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Summary of a conversation (grouped by channel) for listing purposes.
/// </summary>
public sealed record ConversationSummary
{
    /// <summary>Conversation/channel identifier.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Channel identifier.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Display title for the conversation.</summary>
    public required string Title { get; init; }

    /// <summary>Number of visible messages in the conversation.</summary>
    public required int MessageCount { get; init; }

    /// <summary>Timestamp of the most recent message.</summary>
    public required DateTimeOffset LastMessageAt { get; init; }
}
