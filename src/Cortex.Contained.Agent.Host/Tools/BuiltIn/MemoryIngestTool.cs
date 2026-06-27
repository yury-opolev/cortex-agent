using System.Globalization;
using System.Text;
using System.Text.Json;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Store a new memory with automatic deduplication. Content is checked against
/// existing memories using the <see cref="MemoryConsolidationService"/>. If a
/// similar memory already exists, it will be updated/merged instead of creating
/// a duplicate.
/// </summary>
internal sealed class MemoryIngestTool : IAgentTool
{
    private readonly MemoryConsolidationService consolidation;
    private readonly IEmbeddingService embeddingService;
    private readonly IModelProvider modelProvider;

    public MemoryIngestTool(MemoryConsolidationService consolidation, IEmbeddingService embeddingService, IModelProvider modelProvider)
    {
        this.consolidation = consolidation;
        this.embeddingService = embeddingService;
        this.modelProvider = modelProvider;
    }

    public string Name => "memory_ingest";

    public string Description =>
        "Store a new memory. The content will be checked against existing memories to avoid duplicates. " +
        "If similar information already exists, it will be merged automatically. " +
        "Use tags to categorize memories for filtered retrieval.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "content": {
              "type": "string",
              "description": "The full text content to store as a memory."
            },
            "title": {
              "type": "string",
              "description": "Optional short title or label for the memory."
            },
            "tags": {
              "type": "string",
              "description": "Optional tags for categorization, as a JSON array of strings (e.g. [\"project\",\"notes\"])."
            }
          },
          "required": ["content"]
        }
        """;

    public async Task<AgentToolResult> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var availabilityError = await MemoryFormatHelper.CheckEmbeddingAvailabilityAsync(this.embeddingService, cancellationToken).ConfigureAwait(false);
        if (availabilityError is not null)
        {
            return AgentToolResult.Fail(availabilityError);
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("content", out var contentElement))
            {
                return AgentToolResult.Fail("Missing required parameter: content");
            }

            var content = contentElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                return AgentToolResult.Fail("Content cannot be empty.");
            }

            string? title = null;
            if (root.TryGetProperty("title", out var titleElement))
            {
                title = titleElement.GetString();
            }

            var tags = ParseTags(root);

            // Run consolidation — this searches for similar memories and decides
            // whether to ADD, UPDATE, NOOP, or DELETE+REPLACE.
            // Acquire the consolidation lock so we don't race with background extraction.
            var fact = new MemoryConsolidationService.ConsolidationFact
            {
                Content = content,
                Title = title,
                Tags = tags,
            };

            await this.consolidation.AcquireConsolidationLockAsync(cancellationToken).ConfigureAwait(false);
            MemoryConsolidationService.ConsolidationResult result;
            try
            {
                result = await this.consolidation.ConsolidateAsync(
                    fact, this.modelProvider.MemoryModel, context.ConversationId, batchMemories: null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.consolidation.ReleaseConsolidationLock();
            }

            return result.Action switch
            {
                MemoryConsolidationService.ConsolidationAction.Added =>
                    AgentToolResult.Ok($"Memory stored successfully. ID: {result.MemoryId}"),
                MemoryConsolidationService.ConsolidationAction.Updated =>
                    AgentToolResult.Ok($"Similar memory already existed. Updated memory {result.MemoryId} with merged content: {result.Content}"),
                MemoryConsolidationService.ConsolidationAction.Replaced =>
                    AgentToolResult.Ok($"Contradicting memory was replaced. New memory ID: {result.MemoryId}"),
                MemoryConsolidationService.ConsolidationAction.Noop =>
                    AgentToolResult.Ok($"This information is already stored in memory. No action needed. Reason: {result.Reason}"),
                _ => AgentToolResult.Fail($"Memory consolidation returned unexpected result: {result.Reason}"),
            };
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

    internal static List<string>? ParseTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tagsElement))
        {
            return null;
        }

        var tagsJson = tagsElement.GetString();
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(tagsJson);
        }
        catch (JsonException)
        {
            // If not a valid JSON array, treat the whole string as a single tag
            return [tagsJson];
        }
    }
}
