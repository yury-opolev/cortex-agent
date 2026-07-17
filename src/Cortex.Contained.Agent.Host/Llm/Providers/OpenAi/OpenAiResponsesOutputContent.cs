namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// A content part inside a Responses API <c>message</c> output item. Assistant
/// text arrives as <c>output_text</c>.
/// </summary>
internal sealed class OpenAiResponsesOutputContent
{
    public string? Type { get; set; }
    public string? Text { get; set; }
}
