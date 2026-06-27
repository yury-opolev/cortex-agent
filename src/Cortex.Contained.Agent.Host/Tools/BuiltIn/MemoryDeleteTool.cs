using System.Text.Json;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Delete a memory and all its chunks permanently.
/// </summary>
internal sealed class MemoryDeleteTool : IAgentTool
{
    private readonly IMemoryService memoryService;

    public MemoryDeleteTool(IMemoryService memoryService)
    {
        this.memoryService = memoryService;
    }

    public string Name => "memory_delete";

    public string Description =>
        "Delete a memory and all its chunks permanently. This action cannot be undone.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "The memory ID (GUID) to delete."
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
            var deleted = await this.memoryService.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

            return AgentToolResult.Ok(deleted
                    ? $"Memory {id} deleted successfully."
                    : $"Memory not found: {id}");
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }
    }
}
