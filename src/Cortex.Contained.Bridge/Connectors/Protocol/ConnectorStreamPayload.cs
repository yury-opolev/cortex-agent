using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>stream</c> frame — a partial text chunk during streaming.</summary>
public sealed record ConnectorStreamPayload
{
    /// <summary>Conversation receiving the streaming update.</summary>
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>Partial text delta appended to the current response.</summary>
    [JsonPropertyName("delta")]
    public string Delta { get; init; } = string.Empty;
}
