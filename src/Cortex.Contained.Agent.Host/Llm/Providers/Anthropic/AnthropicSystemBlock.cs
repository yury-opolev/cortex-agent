namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

/// <summary>
/// A system prompt content block for the Anthropic Messages API.
/// Supports <c>cache_control</c> for prompt caching.
/// </summary>
internal sealed class AnthropicSystemBlock
{
    public string Type { get; set; } = "text";
    public required string Text { get; set; }
    public AnthropicCacheControl? CacheControl { get; set; }  // snake_case → cache_control
}
