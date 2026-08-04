namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicMessagesRequest
{
    public required string Model { get; set; }
    public required List<AnthropicMessage> Messages { get; set; }

    /// <summary>
    /// System prompt as an array of content blocks (supports cache_control).
    /// Use <see cref="AnthropicSystemBlock"/> elements.
    /// </summary>
    public List<AnthropicSystemBlock>? System { get; set; }

    public int MaxTokens { get; set; } = 8192;
    public bool Stream { get; set; }
    public List<AnthropicTool>? Tools { get; set; }

    /// <summary>
    /// Output controls carrying the reasoning effort. Omitted entirely unless the model accepts
    /// the parameter — sending it to a model that does not is an HTTP 400.
    /// </summary>
    public AnthropicOutputConfig? OutputConfig { get; set; }
}

/// <summary>Anthropic <c>output_config</c>, gated by the effort beta header.</summary>
internal sealed class AnthropicOutputConfig
{
    public required string Effort { get; set; }
}
