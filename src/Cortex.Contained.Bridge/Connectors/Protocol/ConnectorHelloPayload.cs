using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Payload of a <c>hello</c> frame sent by the connector on attach.</summary>
public sealed record ConnectorHelloPayload
{
    /// <summary>Connector type key; null means the frame is missing the field.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Human-readable connector name shown in the Web UI.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>Connector software version string.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Connector instance identifier allowing one key to serve multiple channels.</summary>
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; init; }

    /// <summary>DPAPI-stored pairing token; absent on first connect.</summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>ISO-8601 cursor for replay; omit to receive no replay.</summary>
    [JsonPropertyName("sinceCursor")]
    public string? SinceCursor { get; init; }

    /// <summary>Connector-declared capabilities.</summary>
    [JsonPropertyName("capabilities")]
    public ConnectorCapabilitiesPayload? Capabilities { get; init; }
}
