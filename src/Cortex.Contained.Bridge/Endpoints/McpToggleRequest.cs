using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Master MCP enable-toggle request from the Web UI.</summary>
public sealed class McpToggleRequest
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }
}
