using System.Text.Json.Serialization;

namespace Cortex.Contained.Bridge.Connectors.Protocol;

/// <summary>Text content of a connector message.</summary>
public sealed record ConnectorContentPayload
{
    /// <summary>Message text; null when the frame omits the field.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>When true the text contains Markdown formatting.</summary>
    [JsonPropertyName("isMarkdown")]
    public bool IsMarkdown { get; init; }
}
