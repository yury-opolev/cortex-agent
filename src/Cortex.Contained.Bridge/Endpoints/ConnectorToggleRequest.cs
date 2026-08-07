using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Master connector enable-toggle request from the Web UI.</summary>
public sealed class ConnectorToggleRequest
{
    /// <summary>The requested master switch state. Required.</summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
