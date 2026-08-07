using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Capabilities advertised by the connector in the <c>hello</c> frame.</summary>
public sealed record ConnectorCapabilitiesPayload
{
    /// <summary>Whether the connector supports streaming text responses.</summary>
    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; }

    /// <summary>Whether the connector renders rich (Markdown) text.</summary>
    [JsonPropertyName("richText")]
    public bool RichText { get; init; }

    /// <summary>Whether the connector supports media attachments.</summary>
    [JsonPropertyName("media")]
    public bool Media { get; init; }

    /// <summary>Connector-declared maximum message length in characters; null means unlimited.</summary>
    [JsonPropertyName("maxMessageLength")]
    public int? MaxMessageLength { get; init; }
}
