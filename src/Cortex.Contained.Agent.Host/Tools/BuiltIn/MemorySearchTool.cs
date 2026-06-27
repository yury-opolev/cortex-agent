using System.Globalization;
using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Configuration;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tools.BuiltIn;

/// <summary>
/// Search memories by semantic similarity. Returns the most relevant memories ranked by similarity score.
/// </summary>
internal sealed class MemorySearchTool : IAgentTool
{
    private readonly IMemoryService memoryService;
    private readonly IEmbeddingService embeddingService;
    private readonly MemoryMcpOptions options;

    public MemorySearchTool(IMemoryService memoryService, IEmbeddingService embeddingService, IOptions<MemoryMcpOptions> options)
    {
        this.memoryService = memoryService;
        this.embeddingService = embeddingService;
        this.options = options.Value;
    }

    public string Name => "memory_search";

    public string Description =>
        "Search memories by semantic similarity. Returns the most relevant memories ranked by similarity score. " +
        "Use this to find previously stored information, notes, or context.";

    public string ParametersSchema => """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "The search query text. Memories semantically similar to this text will be returned."
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of results to return (default: 5)."
            },
            "min_score": {
              "type": "number",
              "description": "Minimum similarity score threshold, 0.0 to 1.0 (optional)."
            },
            "tags": {
              "type": "string",
              "description": "Filter by tags: only return memories with at least one matching tag. JSON array of strings (e.g. [\"project\"])."
            }
          },
          "required": ["query"]
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

            if (!root.TryGetProperty("query", out var queryElement))
            {
                return AgentToolResult.Fail("Missing required parameter: query");
            }

            var query = queryElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                return AgentToolResult.Fail("Query cannot be empty.");
            }

            var limit = 5;
            if (root.TryGetProperty("limit", out var limitElement))
            {
                limit = limitElement.GetInt32();
            }

            float? minScore = null;
            if (root.TryGetProperty("min_score", out var minScoreElement))
            {
                minScore = minScoreElement.GetSingle();
            }

            var tags = MemoryIngestTool.ParseTags(root);

            var results = await this.memoryService.SearchAsync(query, limit, minScore, tags, cancellationToken).ConfigureAwait(false);

            if (results.Count == 0)
            {
                return AgentToolResult.Ok("No matching memories found.");
            }

            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Found {results.Count} matching memories:\n");

            foreach (var result in results)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"--- Memory: {result.MemoryId} (Score: {result.Score:F4}) ---");
                if (result.Title is not null)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Title: {result.Title}");
                }

                if (result.Tags.Count > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"Tags: {string.Join(", ", result.Tags)}");
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"Created: {result.CreatedAt:u} | Updated: {result.UpdatedAt:u}");

                // Truncate content if needed
                var resultContent = result.Content;
                if (resultContent.Length > this.options.SearchMaxContentLength)
                {
                    resultContent = resultContent[..this.options.SearchMaxContentLength];
                    sb.AppendLine(resultContent);
                    sb.AppendLine(CultureInfo.InvariantCulture, $"[truncated] Use memory_get with id \"{result.MemoryId}\" to read full content.");
                }
                else
                {
                    sb.AppendLine(resultContent);
                }

                sb.AppendLine();
            }

            return AgentToolResult.Ok(sb.ToString().TrimEnd());
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
