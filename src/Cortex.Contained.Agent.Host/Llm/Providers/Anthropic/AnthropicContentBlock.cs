using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

/// <summary>
/// Polymorphic content block for Anthropic messages API.
/// Which properties are populated depends on <see cref="Type"/>:
///   "text"        → Text
///   "image"       → Source (with Type, MediaType, Data)
///   "tool_use"    → Id, Name, Input
///   "tool_result" → ToolUseId, Content
/// Null properties are omitted by ProviderClientHelpers.JsonOptions.DefaultIgnoreCondition = WhenWritingNull.
/// </summary>
internal sealed class AnthropicContentBlock
{
    public required string Type { get; set; }
    public string? Text { get; set; }
    public AnthropicImageSource? Source { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement? Input { get; set; }
    public string? ToolUseId { get; set; }    // snake_case → tool_use_id
    public string? Content { get; set; }
}
