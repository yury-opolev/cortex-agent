namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
}
