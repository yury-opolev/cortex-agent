namespace Cortex.Contained.Contracts.Messages;

/// <summary>
/// A message from the agent, heading to an external channel.
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>Agent-assigned message ID.</summary>
    public required string MessageId { get; init; }

    /// <summary>Target conversation ID.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Target channel ID.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Message content.</summary>
    public required MessageContent Content { get; init; }

    /// <summary>ID of the message being replied to, if any.</summary>
    public string? ReplyToMessageId { get; init; }

    /// <summary>Thread ID, if targeting a thread.</summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// When true, this message is pre-tool narration ("thinking") rather than the final
    /// answer. Streaming channels (web chat) render it in a dimmed, collapsible lane;
    /// non-streaming channels (Discord/voice) ignore it. Defaults to false.
    /// </summary>
    public bool IsThinking { get; init; }
}
