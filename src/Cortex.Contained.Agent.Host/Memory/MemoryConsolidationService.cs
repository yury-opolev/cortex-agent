using Cortex.Contained.Agent.Host.Agent;
using System.Globalization;
using System.Text.Json;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Shared memory consolidation logic used by both <see cref="MemoryExtractionService"/>
/// (background extraction) and <see cref="Tools.BuiltIn.MemoryIngestTool"/> (explicit agent saves).
/// <para>
/// For a single fact, searches for semantically similar existing memories, then asks the LLM
/// to decide: ADD, UPDATE, DELETE, or NOOP. This ensures all memory write paths go through
/// the same deduplication pipeline.
/// </para>
/// </summary>
public sealed partial class MemoryConsolidationService : IDisposable
{
    private readonly ILlmClient llmClient;
    private readonly IMemoryService memoryService;
    private readonly ILogger<MemoryConsolidationService> logger;

    /// <summary>
    /// Serializes all consolidation operations. Both background extraction and
    /// explicit <c>memory_ingest</c> tool calls go through <see cref="ConsolidateAsync"/>,
    /// so this ensures they never race on the same memory store.
    /// </summary>
    private readonly SemaphoreSlim consolidationLock = new(1, 1);

    /// <summary>How many similar memories to retrieve for conflict resolution.</summary>
    internal const int SimilarMemoryLimit = 5;

    /// <summary>Minimum similarity score to consider a memory as potentially conflicting.</summary>
    internal const float SimilarityThreshold = 0.3f;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public MemoryConsolidationService(
        ILlmClient llmClient,
        IMemoryService memoryService,
        ILogger<MemoryConsolidationService> logger)
    {
        this.llmClient = llmClient;
        this.memoryService = memoryService;
        this.logger = logger;
    }

    // ── Public types ─────────────────────────────────────────────────────

    /// <summary>
    /// Represents a fact to be consolidated against existing memories.
    /// </summary>
    public sealed record ConsolidationFact
    {
        public string Content { get; init; } = string.Empty;
        public string? Title { get; init; }
        public List<string>? Tags { get; init; }
    }

    /// <summary>
    /// Tracks a memory that was added or updated within the current batch,
    /// so subsequent facts can see it even if the embedding search misses it.
    /// </summary>
    public sealed record BatchMemoryEntry(string MemoryId, string? Title, string Content);

    /// <summary>
    /// Result of a consolidation operation.
    /// </summary>
    public sealed record ConsolidationResult
    {
        /// <summary>The action that was taken.</summary>
        public required ConsolidationAction Action { get; init; }

        /// <summary>The memory ID that was created, updated, or deleted.</summary>
        public string? MemoryId { get; init; }

        /// <summary>The final content of the memory (for ADD and UPDATE).</summary>
        public string? Content { get; init; }

        /// <summary>Reason for the decision (from the LLM).</summary>
        public string? Reason { get; init; }
    }

    /// <summary>
    /// Actions the consolidation service can take.
    /// </summary>
    public enum ConsolidationAction
    {
        /// <summary>A new memory was created.</summary>
        Added,

        /// <summary>An existing memory was updated/merged.</summary>
        Updated,

        /// <summary>An existing memory was deleted and replaced.</summary>
        Replaced,

        /// <summary>No action was needed (fact already exists).</summary>
        Noop,

        /// <summary>An error occurred during consolidation.</summary>
        Error,
    }

    // ── Core consolidation logic ─────────────────────────────────────────

    /// <summary>
    /// Consolidates a single fact against existing memories.
    /// Searches for similar memories, combines with optional batch context,
    /// and asks the LLM to decide what to do.
    /// </summary>
    /// <param name="fact">The fact to consolidate.</param>
    /// <param name="model">The LLM model to use for the consolidation decision.</param>
    /// <param name="conversationId">Conversation ID for logging.</param>
    /// <param name="batchMemories">
    /// Optional list of memories added/updated earlier in the same batch.
    /// When provided, entries are included in the LLM context and updated
    /// in-place when the consolidation creates or modifies a memory.
    /// Pass <c>null</c> for single-fact consolidation (e.g., from the ingest tool).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The consolidation result describing what action was taken.</returns>
    /// <summary>
    /// Acquires the consolidation lock. Callers must call <see cref="ReleaseConsolidationLock"/>
    /// when done. Background extraction holds this for the entire batch; the ingest tool
    /// acquires it for a single fact — preventing them from racing on the memory store.
    /// </summary>
    public Task AcquireConsolidationLockAsync(CancellationToken cancellationToken)
        => this.consolidationLock.WaitAsync(cancellationToken);

    /// <summary>Releases the consolidation lock.</summary>
    public void ReleaseConsolidationLock()
        => this.consolidationLock.Release();

    /// <inheritdoc />
    public void Dispose() => this.consolidationLock.Dispose();

    public async Task<ConsolidationResult> ConsolidateAsync(
        ConsolidationFact fact,
        string model,
        string conversationId,
        List<BatchMemoryEntry>? batchMemories,
        CancellationToken cancellationToken)
    {
        // Search for semantically similar existing memories
        var similarMemories = await this.memoryService.SearchAsync(
            fact.Content,
            SimilarMemoryLimit,
            SimilarityThreshold,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Combine search results with memories added earlier in this batch.
        // This ensures the LLM sees recently-added facts even if the embedding
        // search doesn't return them (timing / similarity threshold).
        var combinedMemories = new List<(string MemoryId, string? Title, string Content, float? Score)>();

        // Add search results (deduplicated against batch by ID)
        var searchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in similarMemories)
        {
            searchIds.Add(m.MemoryId);
            combinedMemories.Add((m.MemoryId, m.Title, m.Content, m.Score));
        }

        // Add batch memories not already in search results
        if (batchMemories is not null)
        {
            foreach (var bm in batchMemories)
            {
                if (!searchIds.Contains(bm.MemoryId))
                {
                    combinedMemories.Add((bm.MemoryId, bm.Title, bm.Content, null));
                }
            }
        }

        if (combinedMemories.Count == 0)
        {
            // No similar memories and no batch context — just ADD
            var ingestResult = await this.memoryService.IngestAsync(
                fact.Content, fact.Title, fact.Tags, force: true, cancellationToken).ConfigureAwait(false);
            this.LogMemoryAdded(conversationId, ingestResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);
            batchMemories?.Add(new BatchMemoryEntry(ingestResult.MemoryId!, fact.Title, fact.Content));
            return new ConsolidationResult
            {
                Action = ConsolidationAction.Added,
                MemoryId = ingestResult.MemoryId,
                Content = fact.Content,
                Reason = "no similar memories found",
            };
        }

        // Build context of existing/batch memories for the LLM
        var existingMemoriesText = string.Join("\n", combinedMemories.Select((m, i) =>
        {
            var scoreText = m.Score.HasValue
                ? string.Create(CultureInfo.InvariantCulture, $"Score={m.Score.Value:F3}")
                : "Score=N/A (added in this batch)";
            return string.Create(CultureInfo.InvariantCulture,
                $"[{i}] ID={m.MemoryId}, {scoreText}, Title=\"{m.Title}\", Content=\"{m.Content}\"");
        }));

        var systemPrompt = """
            You are a memory consolidation system. You are given a NEW FACT extracted from a conversation
            and a list of EXISTING MEMORIES that are semantically similar, each with a similarity score.

            Your primary goal is to AVOID DUPLICATES. When in doubt, prefer UPDATE or NOOP over ADD.

            Decide what to do with the new fact:

            - UPDATE: The new fact overlaps with an existing memory — same person, place, topic, or entity.
              Merge the information into a single comprehensive memory. Always prefer this when there is ANY
              topical overlap, even if the details differ. Provide the memory ID and the merged content that
              combines all details from both old and new.
            - NOOP: The new fact is already fully covered by an existing memory. Do nothing.
            - DELETE: The new fact directly contradicts an existing memory, making it obsolete. Provide the
              memory ID to delete. The new fact will then be added separately.
            - ADD: The new fact is about a completely different topic, person, or entity than ALL existing
              memories. Only use ADD when there is truly zero overlap with any existing memory.

            Important guidelines:
            - Even a low similarity score (0.3+) can indicate topical overlap — look at the actual content.
            - If an existing memory mentions the same person, location, project, preference, or entity as the
              new fact, use UPDATE to merge, not ADD.
            - When merging (UPDATE), combine all details from both memories into one clear, comprehensive statement.
            - CRITICAL: NEVER discard historical data points when merging. If the existing memory contains a
              series of events, dates, measurements, or progress over time (e.g., workout sessions, milestones,
              recurring activities), APPEND the new data point to the existing history. Temporal progression
              and trends must be preserved — they are not duplicates, they are a valuable record.

            Respond with exactly one JSON object and nothing else — no commentary, no explanation, no markdown:
            {"action": "UPDATE|NOOP|DELETE|ADD", "memoryId": "id-if-UPDATE-or-DELETE", "mergedContent": "combined-content-if-UPDATE", "reason": "brief explanation"}
            """;

        var userPrompt = string.Create(CultureInfo.InvariantCulture, $"""
            NEW FACT:
            Title: {fact.Title}
            Content: {fact.Content}

            EXISTING MEMORIES:
            {existingMemoriesText}
            """);

        var request = new LlmCompletionRequest
        {
            Model = model,
            Messages =
            [
                new LlmMessage { Role = "system", Content = systemPrompt },
                new LlmMessage { Role = "user", Content = userPrompt },
            ],
            Temperature = 0.0,
            MaxTokens = TokenLimits.Medium,
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "memory-consolidation",
        };

        var result = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            this.LogLlmCallFailed("consolidation", result.ErrorMessage ?? "empty response");
            // Fallback: just ADD to avoid losing the fact (this is a connectivity issue, not a duplicate risk)
            var fallbackResult = await this.memoryService.IngestAsync(
                fact.Content, fact.Title, fact.Tags, force: true, cancellationToken).ConfigureAwait(false);
            this.LogMemoryAdded(conversationId, fallbackResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);
            batchMemories?.Add(new BatchMemoryEntry(fallbackResult.MemoryId!, fact.Title, fact.Content));
            return new ConsolidationResult
            {
                Action = ConsolidationAction.Added,
                MemoryId = fallbackResult.MemoryId,
                Content = fact.Content,
                Reason = "LLM call failed, added as fallback",
            };
        }

        return await ExecuteDecisionAsync(fact, result.Content, conversationId, batchMemories, cancellationToken).ConfigureAwait(false);
    }

    // ── Decision execution ───────────────────────────────────────────────

    /// <summary>
    /// Parses and executes the LLM's consolidation decision.
    /// Updates <paramref name="batchMemories"/> when a new memory is created or an existing one is updated,
    /// so subsequent facts in the same batch can see the latest state.
    /// </summary>
    private async Task<ConsolidationResult> ExecuteDecisionAsync(
        ConsolidationFact fact,
        string llmResponse,
        string conversationId,
        List<BatchMemoryEntry>? batchMemories,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = StripToJson(llmResponse);

            // LLM returned prose instead of JSON — skip to avoid creating duplicates.
            // The fact will be re-extracted in future conversations if it's important.
            if (json.Length == 0 || (json[0] != '{' && json[0] != '['))
            {
                this.LogConsolidationNonJson(json);
                this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", "consolidation returned non-JSON, skipping to avoid duplicate");
                return new ConsolidationResult
                {
                    Action = ConsolidationAction.Noop,
                    Reason = "consolidation returned non-JSON, skipping to avoid duplicate",
                };
            }

            var decision = JsonSerializer.Deserialize<ConsolidationDecision>(json, JsonOptions);
            if (decision is null)
            {
                this.LogParsingFailed("consolidation", json, "deserialized to null");
                return new ConsolidationResult
                {
                    Action = ConsolidationAction.Error,
                    Reason = "failed to parse consolidation decision",
                };
            }

            switch (decision.Action?.ToUpperInvariant())
            {
                case "ADD":
                {
                    var addResult = await this.memoryService.IngestAsync(
                        fact.Content, fact.Title, fact.Tags, force: true, cancellationToken).ConfigureAwait(false);
                    this.LogMemoryAdded(conversationId, addResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);
                    batchMemories?.Add(new BatchMemoryEntry(addResult.MemoryId!, fact.Title, fact.Content));
                    return new ConsolidationResult
                    {
                        Action = ConsolidationAction.Added,
                        MemoryId = addResult.MemoryId,
                        Content = fact.Content,
                        Reason = decision.Reason,
                    };
                }

                case "UPDATE" when !string.IsNullOrEmpty(decision.MemoryId):
                {
                    var content = !string.IsNullOrWhiteSpace(decision.MergedContent)
                        ? decision.MergedContent
                        : fact.Content;
                    await this.memoryService.UpdateAsync(
                        decision.MemoryId, content, cancellationToken: cancellationToken).ConfigureAwait(false);
                    this.LogMemoryUpdated(conversationId, decision.MemoryId, decision.Reason ?? "merged", content);

                    // Update the batch entry if it exists, so subsequent facts see the merged content
                    if (batchMemories is not null)
                    {
                        var existingIdx = batchMemories.FindIndex(b => b.MemoryId == decision.MemoryId);
                        if (existingIdx >= 0)
                        {
                            batchMemories[existingIdx] = new BatchMemoryEntry(
                                decision.MemoryId, fact.Title ?? batchMemories[existingIdx].Title, content);
                        }
                        else
                        {
                            batchMemories.Add(new BatchMemoryEntry(decision.MemoryId, fact.Title, content));
                        }
                    }

                    return new ConsolidationResult
                    {
                        Action = ConsolidationAction.Updated,
                        MemoryId = decision.MemoryId,
                        Content = content,
                        Reason = decision.Reason,
                    };
                }

                case "DELETE" when !string.IsNullOrEmpty(decision.MemoryId):
                {
                    await this.memoryService.DeleteAsync(
                        decision.MemoryId, cancellationToken).ConfigureAwait(false);
                    this.LogMemoryDeleted(conversationId, decision.MemoryId, decision.Reason ?? "contradicted");
                    batchMemories?.RemoveAll(b => b.MemoryId == decision.MemoryId);

                    // After deleting the contradicted memory, ADD the new fact
                    var replaceResult = await this.memoryService.IngestAsync(
                        fact.Content, fact.Title, fact.Tags, force: true, cancellationToken).ConfigureAwait(false);
                    this.LogMemoryAdded(conversationId, replaceResult.MemoryId!, fact.Title ?? "(untitled)", fact.Content);
                    batchMemories?.Add(new BatchMemoryEntry(replaceResult.MemoryId!, fact.Title, fact.Content));
                    return new ConsolidationResult
                    {
                        Action = ConsolidationAction.Replaced,
                        MemoryId = replaceResult.MemoryId,
                        Content = fact.Content,
                        Reason = decision.Reason,
                    };
                }

                case "NOOP":
                {
                    this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", decision.Reason ?? "already exists");
                    return new ConsolidationResult
                    {
                        Action = ConsolidationAction.Noop,
                        Reason = decision.Reason,
                    };
                }

                default:
                {
                    var unknownAction = decision.Action ?? "(null)";
                    this.LogParsingFailed("consolidation", unknownAction, "unknown action");
                    this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", "unknown action, skipping to avoid duplicate");
                    return new ConsolidationResult
                    {
                        Action = ConsolidationAction.Noop,
                        Reason = $"unknown action '{unknownAction}', skipping to avoid duplicate",
                    };
                }
            }
        }
        catch (JsonException ex)
        {
            this.LogParsingFailed("consolidation", llmResponse, ex.Message);
            this.LogMemoryNoop(conversationId, fact.Title ?? "(untitled)", "JSON parse error, skipping to avoid duplicate");
            return new ConsolidationResult
            {
                Action = ConsolidationAction.Noop,
                Reason = "JSON parse error, skipping to avoid duplicate",
            };
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    private sealed record ConsolidationDecision
    {
        public string? Action { get; init; }
        public string? MemoryId { get; init; }
        public string? MergedContent { get; init; }
        public string? Reason { get; init; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Strips markdown code fences and leading/trailing whitespace from an LLM response.
    /// </summary>
    internal static string StripToJson(string llmResponse)
    {
        var json = llmResponse.Trim();

        // Strip markdown code fences: ```json ... ``` or ``` ... ```
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = json.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
            {
                // Complete fence pair — strip both
                json = json[(firstNewline + 1)..lastFence].Trim();
            }
            else if (firstNewline > 0)
            {
                // Opening fence only (truncated response) — strip the ```json line
                json = json[(firstNewline + 1)..].Trim();
            }
        }

        // Find and extract the first complete JSON object or array.
        // Handles leading prose ("Here you go: {…}") and trailing commentary ("{…}\nThis updates…").
        return ExtractFirstJson(json);
    }

    /// <summary>
    /// Scans for the first <c>{</c> or <c>[</c>, then tracks balanced braces/brackets
    /// (respecting JSON strings) to find the matching close. Returns the extracted JSON,
    /// or the original text if no balanced JSON is found.
    /// </summary>
    private static string ExtractFirstJson(string text)
    {
        // Find the first { or [
        var start = -1;
        char open = '{', close = '}';
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                start = i;
                open = '{';
                close = '}';
                break;
            }
            if (text[i] == '[')
            {
                start = i;
                open = '[';
                close = ']';
                break;
            }
        }

        if (start < 0)
        {
            return text; // No JSON delimiter found — return as-is for the caller to handle
        }

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (c == open)
            {
                depth++;
            }
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        // Unbalanced — return from start onwards, let JsonSerializer report the error
        return text[start..];
    }

    // ── LoggerMessage source-generated methods ──────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM call failed during {Phase}: {ErrorMessage}")]
    private partial void LogLlmCallFailed(string phase, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consolidation LLM returned prose instead of JSON, skipping. Full response: {LlmResponse}")]
    private partial void LogConsolidationNonJson(string llmResponse);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse {Phase} JSON — response: {Response} — error: {ErrorMessage}")]
    private partial void LogParsingFailed(string phase, string response, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory ADD: {MemoryId} \"{Title}\" — {Content}")]
    private partial void LogMemoryAdded(string conversationId, string memoryId, string title, string content);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory UPDATE: {MemoryId} — {Reason} — new content: {Content}")]
    private partial void LogMemoryUpdated(string conversationId, string memoryId, string reason, string content);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory DELETE: {MemoryId} — {Reason}")]
    private partial void LogMemoryDeleted(string conversationId, string memoryId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory NOOP: \"{Title}\" — {Reason}")]
    private partial void LogMemoryNoop(string conversationId, string title, string reason);
}
