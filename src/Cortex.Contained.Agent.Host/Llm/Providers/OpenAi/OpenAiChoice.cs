namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiChoice
{
    public OpenAiMessage? Message { get; set; }
    public string? FinishReason { get; set; }
}
