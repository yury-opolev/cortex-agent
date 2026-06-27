namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

/// <summary>Cache control hint for Anthropic prompt caching.</summary>
internal sealed class AnthropicCacheControl
{
    public string Type { get; set; } = "ephemeral";
}
