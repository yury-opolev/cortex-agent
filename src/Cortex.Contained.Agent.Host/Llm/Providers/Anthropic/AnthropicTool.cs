using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Llm.Providers.Anthropic;

internal sealed class AnthropicTool
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public JsonElement? InputSchema { get; set; }   // snake_case → input_schema
    public AnthropicCacheControl? CacheControl { get; set; }  // snake_case → cache_control
}
