namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiToolCall
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public OpenAiFunction? Function { get; set; }
}
