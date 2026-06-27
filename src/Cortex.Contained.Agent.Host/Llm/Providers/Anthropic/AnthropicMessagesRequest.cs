namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicMessagesRequest
{
    public required string Model { get; set; }
    public required List<AnthropicMessage> Messages { get; set; }

    /// <summary>
    /// System prompt as an array of content blocks (supports cache_control).
    /// Use <see cref="AnthropicSystemBlock"/> elements.
    /// </summary>
    public List<AnthropicSystemBlock>? System { get; set; }

    public int MaxTokens { get; set; } = 8192;
    public bool Stream { get; set; }
    public List<AnthropicTool>? Tools { get; set; }
}
