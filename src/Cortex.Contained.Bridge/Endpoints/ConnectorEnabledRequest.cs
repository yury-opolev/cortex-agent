using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Per-connector enable/disable request from the Web UI.</summary>
public sealed class ConnectorEnabledRequest
{
    /// <summary>The requested enabled state for a single paired connector. Required.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
