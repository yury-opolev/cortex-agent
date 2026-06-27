namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiStreamToolCall
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Type { get; set; }
    public OpenAiFunction? Function { get; set; }
}
