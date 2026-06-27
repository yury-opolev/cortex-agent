using System.Text.Json;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Update an existing memory. If content is provided, it will be re-chunked and re-embedded.
/// Title and tags can be updated independently.
/// </summary>
internal sealed class MemoryUpdateTool : IAgentTool
{
    private readonly IMemoryService memoryService;
    private readonly IEmbeddingService embeddingService;

    public MemoryUpdateTool(IMemoryService memoryService, IEmbeddingService embeddingService)
    {
        this.memoryService = memoryService;
        this.embeddingService = embeddingService;
    }

    public string Name => "memory_update";

    public string Description =>
        "Update an existing memory. If content is provided, it will be re-chunked and re-embedded. " +
        "Title and tags can be updated independently without re-embedding.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "id": {
              "type": "string",
              "description": "The memory ID (GUID) to update."
            },
            "content": {
              "type": "string",
              "description": "New content text. If provided, the memory will be re-chunked and re-embedded."
            },
            "title": {
              "type": "string",
              "description": "New title for the memory."
            },
            "tags": {
              "type": "string",
              "description": "New tags as a JSON array of strings (e.g. [\"project\",\"notes\"])."
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

            string? content = null;
            if (root.TryGetProperty("content", out var contentElement))
            {
                content = contentElement.GetString();
            }

            string? title = null;
            if (root.TryGetProperty("title", out var titleElement))
            {
                title = titleElement.GetString();
            }

            var tags = MemoryIngestTool.ParseTags(root);

            if (content is null && title is null && tags is null)
            {
                return AgentToolResult.Fail("No updates provided. Specify at least one of: content, title, or tags.");
            }

            // Content changes require re-embedding — check Ollama availability
            if (content is not null)
            {
                var availabilityError = await MemoryFormatHelper.CheckEmbeddingAvailabilityAsync(this.embeddingService, cancellationToken).ConfigureAwait(false);
                if (availabilityError is not null)
                {
                    return AgentToolResult.Fail(availabilityError);
                }
            }

            var result = await this.memoryService.UpdateAsync(id, content, title, tags, cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return AgentToolResult.Ok($"Memory not found: {id}");
            }

            return AgentToolResult.Ok($"Memory updated successfully.\n\n{MemoryFormatHelper.FormatMemoryResult(result)}");
        }
        catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException)
        {
            return AgentToolResult.Fail($"Embedding service error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return AgentToolResult.Fail($"Invalid JSON arguments: {ex.Message}");
        }
    }
}
