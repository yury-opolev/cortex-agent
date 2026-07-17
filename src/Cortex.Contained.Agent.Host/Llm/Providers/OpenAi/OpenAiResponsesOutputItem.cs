namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// A single item in the Responses API <c>output</c> array (non-streaming) or the
/// <c>item</c> payload of an <c>output_item.added</c>/<c>output_item.done</c>
/// streaming event. The <see cref="Type"/> discriminator selects which fields
/// apply: <c>message</c> uses <see cref="Role"/>/<see cref="Content"/>, while
/// <c>function_call</c> uses <see cref="CallId"/>/<see cref="Name"/>/<see cref="Arguments"/>.
/// </summary>
internal sealed class OpenAiResponsesOutputItem
{
    public string? Type { get; set; }

    // ── message ──
    public string? Role { get; set; }
    public List<OpenAiResponsesOutputContent>? Content { get; set; }

    // ── function_call ──
    public string? CallId { get; set; }
    public string? Name { get; set; }

    /// <summary>Tool arguments as a raw JSON string.</summary>
    public string? Arguments { get; set; }
}
