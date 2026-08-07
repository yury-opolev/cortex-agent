using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>paired</c> frame confirming successful pairing.</summary>
public sealed record ConnectorPairedPayload
{
    /// <summary>DPAPI-encrypted token the connector must present on future connects.</summary>
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    /// <summary>Assigned channel ID for this connector instance.</summary>
    [JsonPropertyName("channelId")]
    public string ChannelId { get; init; } = string.Empty;
}
