namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicMessagesResponse
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Role { get; set; }
    public List<AnthropicContentBlock>? Content { get; set; }
    public string? StopReason { get; set; }         // snake_case → stop_reason
    public AnthropicUsage? Usage { get; set; }
}
