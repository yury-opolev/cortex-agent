namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiChatResponse
{
    public List<OpenAiChoice>? Choices { get; set; }
    public OpenAiUsage? Usage { get; set; }
}
