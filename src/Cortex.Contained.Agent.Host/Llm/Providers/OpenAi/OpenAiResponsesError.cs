namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>Error detail on a failed Responses API response or SSE error event.</summary>
internal sealed class OpenAiResponsesError
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}
