using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>pairing_denied</c> frame.</summary>
public sealed record ConnectorPairingDeniedPayload
{
    /// <summary>Human-readable reason the pairing was denied.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}
