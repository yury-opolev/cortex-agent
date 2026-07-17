using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>A Responses API function call output item (tool result).</summary>
internal sealed class OpenAiResponsesFunctionCallOutputItem : OpenAiResponsesInputItem
{
    [JsonIgnore]
    public override string Type => "function_call_output";

    public required string CallId { get; set; }  // snake_case → call_id

    /// <summary>Tool result text returned to the model.</summary>
    public required string Output { get; set; }
}
