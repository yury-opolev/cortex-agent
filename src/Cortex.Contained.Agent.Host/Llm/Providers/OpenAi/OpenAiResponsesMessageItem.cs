using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>A Responses API message item (role + content parts).</summary>
internal sealed class OpenAiResponsesMessageItem : OpenAiResponsesInputItem
{
    [JsonIgnore]
    public override string Type => "message";

    public required string Role { get; set; }

    public required IReadOnlyList<OpenAiResponsesContentPart> Content { get; set; }
}
