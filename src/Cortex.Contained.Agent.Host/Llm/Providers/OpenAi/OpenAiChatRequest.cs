using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiChatRequest
{
    public required string Model { get; set; }
    public required List<OpenAiMessage> Messages { get; set; }
    public double? Temperature { get; set; }
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }
    [JsonPropertyName("max_completion_tokens")]
    public int? MaxCompletionTokens { get; set; }
    public bool Stream { get; set; }
    public List<OpenAiToolDefinition>? Tools { get; set; }
    public OpenAiStreamOptions? StreamOptions { get; set; }
}
