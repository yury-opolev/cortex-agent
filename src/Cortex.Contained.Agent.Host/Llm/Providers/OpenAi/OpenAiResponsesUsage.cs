using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Token usage reported by the OpenAI Responses API. Unlike Chat Completions
/// (<c>prompt_tokens</c>/<c>completion_tokens</c>), Responses reports
/// <c>input_tokens</c>/<c>output_tokens</c>.
/// </summary>
internal sealed class OpenAiResponsesUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>Prompt-token breakdown, including the cached subset.</summary>
    public OpenAiResponsesTokenDetails? InputTokensDetails { get; set; }

    /// <summary>Projects onto the shared contract usage record.</summary>
    public LlmTokenUsage ToTokenUsage()
    {
        var (fresh, cached) = OpenAiCachedTokens.Split(this.InputTokens, this.InputTokensDetails);

        return new LlmTokenUsage
        {
            PromptTokens = fresh,
            CompletionTokens = this.OutputTokens,
            TotalTokens = this.TotalTokens,
            CacheReadTokens = cached,
        };
    }
}

/// <summary>
/// Splits an OpenAI prompt-token total into its fresh and cached parts. OpenAI reports the
/// cached count as a SUBSET of the total (the inverse of Anthropic), so reporting the total as
/// fresh input would overstate prompt cost and hide whether prompt caching is working at all.
/// </summary>
internal static class OpenAiCachedTokens
{
    internal static (int Fresh, int Cached) Split(int totalInputTokens, OpenAiResponsesTokenDetails? details)
    {
        var cached = details?.CachedTokens ?? 0;
        if (cached <= 0)
        {
            return (totalInputTokens, 0);
        }

        // Clamp: a provider reporting more cached than total must not yield negative input.
        var clamped = Math.Min(cached, totalInputTokens);
        return (totalInputTokens - clamped, clamped);
    }
}
