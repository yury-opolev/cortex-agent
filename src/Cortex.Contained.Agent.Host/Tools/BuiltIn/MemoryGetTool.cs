using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Retrieve a memory by its ID, including full content, title, tags, and timestamps.
/// </summary>
internal sealed class MemoryGetTool : IAgentTool
{
    private readonly IMemoryService memoryService;

    public MemoryGetTool(IMemoryService memoryService)
    {
        this.memoryService = memoryService;
    }

    public string Name => "memory_get";

    public string Description =>
        "Retrieve a memory by its ID. Returns the full content, title, tags, and timestamps.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "The memory ID (GUID) to retrieve."
            }
          },
          "required": ["id"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("id", out var idElement))
            {
                return AgentToolResult.Fail("Missing required parameter: id");
            }

            var id = idElement.GetString() ?? string.Empty;
            var result = await this.memoryService.GetAsync(id, cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return AgentToolResult.Ok($"Memory not found: {id}");
            }

            return AgentToolResult.Ok(MemoryFormatHelper.FormatMemoryResult(result));
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }
    }
}
