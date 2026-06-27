namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicMessage
{
    public required string Role { get; set; }
    public required List<AnthropicContentBlock> Content { get; set; }
}
