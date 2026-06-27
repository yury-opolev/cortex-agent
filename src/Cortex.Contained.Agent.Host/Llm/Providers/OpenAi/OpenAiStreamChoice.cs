namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiStreamChoice
{
    public OpenAiStreamDelta? Delta { get; set; }
    public string? FinishReason { get; set; }
}
