namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

/// <summary>Image source for Anthropic's vision API (base64-encoded).</summary>
internal sealed class AnthropicImageSource
{
    public required string Type { get; set; }       // "base64"
    public required string MediaType { get; set; }  // snake_case → media_type
    public required string Data { get; set; }       // base64 string
}
