namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// Why a Responses run stopped short. <see cref="Reason"/> is
/// <c>max_output_tokens</c> when the output token budget was exhausted.
/// </summary>
internal sealed class OpenAiResponsesIncompleteDetails
{
    public string? Reason { get; set; }
}
