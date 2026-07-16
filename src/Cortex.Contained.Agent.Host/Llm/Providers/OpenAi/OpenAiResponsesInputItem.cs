using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>
/// An item in the OpenAI Responses API <c>input</c> array. The concrete subtype
/// determines the emitted <c>type</c> discriminator (message, function_call,
/// function_call_output). <see cref="Type"/> is exposed for code/tests only;
/// serialization writes the discriminator via <see cref="JsonPolymorphicAttribute"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAiResponsesMessageItem), "message")]
[JsonDerivedType(typeof(OpenAiResponsesFunctionCallItem), "function_call")]
[JsonDerivedType(typeof(OpenAiResponsesFunctionCallOutputItem), "function_call_output")]
internal abstract class OpenAiResponsesInputItem
{
    /// <summary>Responses item type discriminator.</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}
