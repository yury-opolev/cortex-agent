namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicSseError
{
    public string? Type { get; set; }
    public string? Message { get; set; }
}
