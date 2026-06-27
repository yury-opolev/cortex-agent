namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicStreamDelta
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? PartialJson { get; set; }    // snake_case → partial_json
    public string? StopReason { get; set; }     // snake_case → stop_reason
}
