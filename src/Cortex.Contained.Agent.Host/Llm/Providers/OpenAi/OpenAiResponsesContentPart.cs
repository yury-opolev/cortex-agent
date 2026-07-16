using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>A Responses API content part (input_text, output_text, input_image).</summary>
internal sealed class OpenAiResponsesContentPart
{
    public required string Type { get; set; }

    /// <summary>Text content (when Type is "input_text" or "output_text").</summary>
    public string? Text { get; set; }

    /// <summary>Data URI for the image (when Type is "input_image").</summary>
    public string? ImageUrl { get; set; }  // snake_case → image_url
}
