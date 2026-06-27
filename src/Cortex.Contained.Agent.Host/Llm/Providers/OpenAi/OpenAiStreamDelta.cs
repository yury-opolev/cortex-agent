namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiStreamDelta
{
    public string? Role { get; set; }
    public string? Content { get; set; }
    public List<OpenAiStreamToolCall>? ToolCalls { get; set; }
}
