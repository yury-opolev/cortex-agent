using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>ready</c> frame sent by the Bridge once the connector is accepted.</summary>
public sealed record ConnectorReadyPayload
{
    /// <summary>Assigned channel ID for this connector instance.</summary>
    [JsonPropertyName("channelId")]
    public string ChannelId { get; init; } = string.Empty;

    /// <summary>Number of messages replayed before entering steady state.</summary>
    [JsonPropertyName("replayCount")]
    public int ReplayCount { get; init; }
}
