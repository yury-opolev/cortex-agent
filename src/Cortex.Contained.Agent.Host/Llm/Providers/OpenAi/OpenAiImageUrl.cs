namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>Image URL reference for OpenAI vision API.</summary>
internal sealed class OpenAiImageUrl
{
    /// <summary>Data URL: "data:{mediaType};base64,{data}" or a regular URL.</summary>
    public required string Url { get; set; }
}
