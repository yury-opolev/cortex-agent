using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>A Responses API function call item (assistant-requested tool call).</summary>
internal sealed class OpenAiResponsesFunctionCallItem : OpenAiResponsesInputItem
{
    [JsonIgnore]
    public override string Type => "function_call";

    public required string CallId { get; set; }  // snake_case → call_id

    public required string Name { get; set; }

    /// <summary>Tool arguments as a raw JSON string.</summary>
    public required string Arguments { get; set; }
}
