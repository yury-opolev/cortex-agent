namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiStreamResponse
{
    public List<OpenAiStreamChoice>? Choices { get; set; }
    public OpenAiUsage? Usage { get; set; }
}
