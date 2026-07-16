namespace Cortex.Contained.Agent.Host.Llm.Providers.Copilot;

/// <summary>
/// The GitHub Copilot response API a model should be driven through, in ascending
/// capability order. Resolved from a model's provider-reported endpoint metadata by
/// <see cref="CopilotEndpointResolver"/>.
/// </summary>
public enum CopilotEndpoint
{
    /// <summary>OpenAI-style Chat Completions (<c>/chat/completions</c>). Default fallback.</summary>
    ChatCompletions,

    /// <summary>Anthropic-style Messages (<c>/v1/messages</c>).</summary>
    Messages,

    /// <summary>OpenAI-style Responses (<c>/responses</c>).</summary>
    Responses,
}
