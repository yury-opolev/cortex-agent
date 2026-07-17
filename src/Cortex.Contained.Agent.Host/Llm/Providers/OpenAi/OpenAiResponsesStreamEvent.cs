namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// A single OpenAI Responses API streaming event (the JSON payload of one
/// <c>data:</c> line). Dispatch is on <see cref="Type"/> (e.g.
/// <c>response.output_text.delta</c>, <c>response.output_item.added</c>,
/// <c>response.function_call_arguments.delta</c>, <c>response.completed</c>);
/// only the fields relevant to that event type are populated.
/// </summary>
internal sealed class OpenAiResponsesStreamEvent
{
    public string? Type { get; set; }

    /// <summary>Text or argument fragment for <c>*.delta</c> events.</summary>
    public string? Delta { get; set; }

    /// <summary>Index of the output item this event pertains to.</summary>
    public int? OutputIndex { get; set; }

    /// <summary>Server-assigned output item ID.</summary>
    public string? ItemId { get; set; }

    /// <summary>Full item payload for <c>output_item.added</c>/<c>output_item.done</c>.</summary>
    public OpenAiResponsesOutputItem? Item { get; set; }

    /// <summary>Snapshot for terminal events (<c>completed</c>/<c>incomplete</c>/<c>failed</c>).</summary>
    public OpenAiResponsesResponse? Response { get; set; }

    /// <summary>Top-level message on an <c>error</c> event.</summary>
    public string? Message { get; set; }

    /// <summary>Top-level code on an <c>error</c> event.</summary>
    public string? Code { get; set; }
}
