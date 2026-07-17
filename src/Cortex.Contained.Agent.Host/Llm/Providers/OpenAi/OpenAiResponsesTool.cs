using System.Text.Json;

namespace Cortex.Contained.Agent.Host.Llm.Providers.OpenAi;

/// <summary>A flat Responses API function tool definition.</summary>
internal sealed class OpenAiResponsesTool
{
    public required string Type { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>JSON Schema for the tool parameters.</summary>
    public JsonElement? Parameters { get; set; }
}
