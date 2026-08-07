using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Identifies the sender of an inbound message.</summary>
public sealed record ConnectorSenderPayload
{
    /// <summary>Sender ID as known to the connector.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Human-readable sender name.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }
}
