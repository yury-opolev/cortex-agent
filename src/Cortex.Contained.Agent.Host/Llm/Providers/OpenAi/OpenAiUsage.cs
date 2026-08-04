using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>Prompt-token breakdown, including the cached subset.</summary>
    public OpenAiResponsesTokenDetails? PromptTokensDetails { get; set; }

    /// <summary>Projects onto the shared contract usage record.</summary>
    public LlmTokenUsage ToTokenUsage()
    {
        var (fresh, cached) = OpenAiCachedTokens.Split(this.PromptTokens, this.PromptTokensDetails);

        return new LlmTokenUsage
        {
            PromptTokens = fresh,
            CompletionTokens = this.CompletionTokens,
            TotalTokens = this.TotalTokens,
            CacheReadTokens = cached,
        };
    }
}
