namespace Cortex.Contained.Bridge.Connectors.Replay;

/// <summary>A single outbound message eligible for replay to a reconnecting connector.</summary>
public sealed record ConnectorReplayMessage
{
    /// <summary>Agent-assigned message identifier.</summary>
    public required string MessageId { get; init; }

    /// <summary>Conversation the message belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Message text content.</summary>
    public required string Text { get; init; }

    /// <summary>Time the message was stored by the agent.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
