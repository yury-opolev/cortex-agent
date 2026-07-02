using System.Text.Json.Serialization;

namespace Cortex.Contained.Channels.CloudMessaging.Envelope;

/// <summary>
/// Wire envelope for the AI Messenger cloud service (design §8).
/// All frames — browser ↔ Web PubSub ↔ bridge — share this shape.
/// camelCase JSON; type values are kebab-case; from values are lowercase.
/// </summary>
public sealed class CloudEnvelope
{
    /// <summary>Protocol version. Currently 1.</summary>
    [JsonPropertyName("v")]
    public int V { get; init; } = 1;

    /// <summary>Frame type (kebab-case): text, stream-chunk, finalize, typing, control, error.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Tenant the message belongs to. Validated on receipt.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Conversation ID (stable per user–agent session).</summary>
    [JsonPropertyName("conversationId")]
    public required string ConversationId { get; init; }

    /// <summary>Originator: "user", "agent", or "system".</summary>
    [JsonPropertyName("from")]
    public required string From { get; init; }

    /// <summary>Per-frame unique ID.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Source timestamp (Unix ms).</summary>
    [JsonPropertyName("ts")]
    public long Ts { get; init; }

    /// <summary>Type-specific payload. May be null for frames that carry no payload (e.g. typing).</summary>
    [JsonPropertyName("payload")]
    public CloudPayload? Payload { get; init; }
}
