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

    /// <summary>Projects onto the shared contract usage record.</summary>
    public LlmTokenUsage ToTokenUsage() => new()
    {
        PromptTokens = this.InputTokens,
        CompletionTokens = this.OutputTokens,
        TotalTokens = this.TotalTokens,
    };
}
