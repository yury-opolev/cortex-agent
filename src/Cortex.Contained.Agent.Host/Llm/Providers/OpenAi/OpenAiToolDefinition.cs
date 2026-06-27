namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiToolDefinition
{
    public required string Type { get; set; }
    public required OpenAiFunctionDefinition Function { get; set; }
}
