using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

internal sealed class OpenAiFunctionDefinition
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public JsonElement? Parameters { get; set; }
}
