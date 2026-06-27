namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>Content part for OpenAI multimodal messages.</summary>
internal sealed class OpenAiContentPart
{
    public required string Type { get; set; }

    /// <summary>Text content (when type = "text").</summary>
    public string? Text { get; set; }

    /// <summary>Image URL object (when type = "image_url").</summary>
    public OpenAiImageUrl? ImageUrl { get; set; }  // snake_case → image_url
}
