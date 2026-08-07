using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>pairing_required</c> frame sent when the connector must be approved.</summary>
public sealed record ConnectorPairingRequiredPayload
{
    /// <summary>Pairing code to be displayed to the user.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>UTC time at which the pairing code expires.</summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }
}
