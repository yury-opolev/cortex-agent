namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicSseEvent
{
    public string? Type { get; set; }
    public int? Index { get; set; }
    public AnthropicContentBlock? ContentBlock { get; set; }  // snake_case → content_block
    public AnthropicStreamDelta? Delta { get; set; }
    public AnthropicUsage? Usage { get; set; }
    public AnthropicSseMessage? Message { get; set; }
    public AnthropicSseError? Error { get; set; }
}
