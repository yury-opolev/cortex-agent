namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Breakdown of the prompt-token count. OpenAI reports the cached portion as a SUBSET of the
/// total input, so it must be subtracted rather than added (the inverse of Anthropic, where
/// cache_read_input_tokens is reported alongside a separate uncached input count).
/// </summary>
internal sealed class OpenAiResponsesTokenDetails
{
    public int CachedTokens { get; set; }
}
