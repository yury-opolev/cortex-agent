using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of an <c>inbound</c> frame — a user message from the connector.</summary>
public sealed record ConnectorInboundPayload
{
    /// <summary>Conversation this message belongs to; null means the field was absent.</summary>
    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; init; }

    /// <summary>Connector-assigned message ID; null means the field was absent.</summary>
    [JsonPropertyName("messageId")]
    public string? MessageId { get; init; }

    /// <summary>Message sender; null when the field is absent.</summary>
    [JsonPropertyName("sender")]
    public ConnectorSenderPayload? Sender { get; init; }

    /// <summary>Message content; null when the field is absent.</summary>
    [JsonPropertyName("content")]
    public ConnectorContentPayload? Content { get; init; }
}
