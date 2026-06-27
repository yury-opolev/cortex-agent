namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicUsage
{
    public int InputTokens { get; set; }                // snake_case → input_tokens
    public int OutputTokens { get; set; }               // snake_case → output_tokens
    public int CacheCreationInputTokens { get; set; }   // snake_case → cache_creation_input_tokens
    public int CacheReadInputTokens { get; set; }       // snake_case → cache_read_input_tokens
}
