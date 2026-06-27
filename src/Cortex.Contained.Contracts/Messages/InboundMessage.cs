using Cortex.Contained.Contracts.Channels;

namespace Cortex.Contained.Contracts.Messages;

/// <summary>
/// A message received from an external channel, heading toward the agent.
/// </summary>
public sealed record InboundMessage
{
    /// <summary>Unique message ID (from the source channel).</summary>
    public required string MessageId { get; init; }

    /// <summary>Channel-specific conversation/chat ID.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The channel that received this message.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Channel type for routing.</summary>
    public required ChannelType ChannelType { get; init; }

    /// <summary>Information about the sender.</summary>
    public required SenderInfo Sender { get; init; }

    /// <summary>Message content.</summary>
    public required MessageContent Content { get; init; }

    /// <summary>When the message was sent (source timestamp).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>ID of the message being replied to, if any.</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>Thread ID, if in a thread.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Whether the message is from a group/channel (vs. DM).</summary>
    public bool IsGroup { get; init; }

    /// <summary>Channel-specific properties.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>Information about the sender of a message.</summary>
public sealed record SenderInfo
{
    /// <summary>Channel-specific sender ID.</summary>
    public required string Id { get; init; }

    /// <summary>Display name (if available).</summary>
    public string? DisplayName { get; init; }

    /// <summary>Whether this sender is verified/paired.</summary>
    public bool IsVerified { get; init; }
}
