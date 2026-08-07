using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>typing</c> frame — indicates the agent is composing a reply.</summary>
public sealed record ConnectorTypingPayload
{
    /// <summary>Conversation for which the typing indicator is active.</summary>
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; init; } = string.Empty;
}
