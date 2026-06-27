namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiMessage
{
    public required string Role { get; set; }

    /// <summary>
    /// Message content. Either a plain string (text-only) or a
    /// <c>List&lt;OpenAiContentPart&gt;</c> (multimodal with images).
    /// System.Text.Json serializes both correctly as the OpenAI API expects.
    /// </summary>
    public object? Content { get; set; }

    public List<OpenAiToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
}
