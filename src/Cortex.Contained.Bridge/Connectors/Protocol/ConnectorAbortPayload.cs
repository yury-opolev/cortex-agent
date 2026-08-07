using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of an <c>abort</c> frame — cancels an in-flight generation.</summary>
public sealed record ConnectorAbortPayload
{
    /// <summary>Conversation to abort; null cancels any active generation.</summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; init; }
}
