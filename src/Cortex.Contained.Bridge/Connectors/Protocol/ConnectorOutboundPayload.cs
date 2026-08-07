using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of an <c>outbound</c> frame — an agent response sent to the connector.</summary>
public sealed record ConnectorOutboundPayload
{
    /// <summary>Target conversation ID.</summary>
    [JsonPropertyName("conversationId")]
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>Agent-assigned message ID.</summary>
    [JsonPropertyName("messageId")]
    public string MessageId { get; init; } = string.Empty;

    /// <summary>Message content.</summary>
    [JsonPropertyName("content")]
    public ConnectorContentPayload Content { get; init; } = new();

    /// <summary>When true this is pre-tool narration rather than the final answer.</summary>
    [JsonPropertyName("isThinking")]
    public bool IsThinking { get; init; }

    /// <summary>Replay cursor for this message; null in live (non-replay) frames.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }
}
