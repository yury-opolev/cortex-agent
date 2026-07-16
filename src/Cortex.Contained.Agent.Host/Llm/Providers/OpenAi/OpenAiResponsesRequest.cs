namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Request body for the OpenAI Responses API (<c>/responses</c>). Deliberately
/// omits token-budget fields (<c>max_output_tokens</c>/<c>max_tokens</c>) so the
/// reasoning budget is not exhausted before visible output, and omits temperature
/// (modern reasoning models reject non-default values).
/// </summary>
internal sealed class OpenAiResponsesRequest
{
    public required string Model { get; set; }

    public bool Stream { get; set; }

    /// <summary>System guidance, combined from all system messages.</summary>
    public string? Instructions { get; set; }

    /// <summary>Ordered conversation items (messages, tool calls, tool results).</summary>
    public required IReadOnlyList<OpenAiResponsesInputItem> Input { get; set; }

    public IReadOnlyList<OpenAiResponsesTool>? Tools { get; set; }
}
