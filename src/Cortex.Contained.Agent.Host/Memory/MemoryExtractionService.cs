using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Llm;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Automatic memory extraction and consolidation, inspired by the Mem0 architecture.
/// Runs as a hosted <see cref="BackgroundService"/> with a bounded
/// <see cref="Channel{T}"/> queue. Message pairs are enqueued by
/// <see cref="AgentRuntime"/> after each response and processed sequentially
/// by a single consumer — eliminating race conditions between concurrent
/// extractions.
/// <para>
/// Two-phase pipeline per message pair:
/// 1. <b>Extraction</b>: An LLM call extracts salient facts from the pair.
/// 2. <b>Consolidation</b>: For each fact, the <see cref="MemoryConsolidationService"/>
///    searches for similar existing memories and decides: ADD, UPDATE, DELETE, or NOOP.
/// </para>
/// </summary>
public sealed partial class MemoryExtractionService : BackgroundService
{
    private readonly ILlmClient llmClient;
    private readonly IEmbeddingService embeddingService;
    private readonly MemoryConsolidationService consolidation;
    private readonly AgentMetrics? metrics;
    private readonly ILogger<MemoryExtractionService> logger;

    /// <summary>Maximum facts to extract from a single message pair (safety limit).</summary>
    private const int MaxFactsPerExtraction = 10;

    /// <summary>Maximum queued message pairs before dropping new ones.</summary>
    private const int MaxQueueSize = 64;

    /// <summary>Maximum characters per extraction chunk (~20k tokens).</summary>
    private const int ExtractionChunkSizeChars = 80_000;

    /// <summary>Number of overlapping entries between consecutive chunks (2 full exchanges).</summary>
    private const int ExtractionChunkOverlapEntries = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Channel<ExtractionWorkItem> channel = Channel.CreateBounded<ExtractionWorkItem>(
        new BoundedChannelOptions(MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Number of items enqueued but not yet fully processed.</summary>
    private int pendingCount;

    /// <summary>Lock protecting <see cref="this.idleTcs"/>.</summary>
    private readonly object idleLock = new();

    /// <summary>
    /// Completed when <see cref="this.pendingCount"/> drops to zero.
    /// Reset each time a new item is enqueued while idle.
    /// </summary>
    private TaskCompletionSource idleTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MemoryExtractionService(
        ILlmClient llmClient,
        IEmbeddingService embeddingService,
        MemoryConsolidationService consolidation,
        ILogger<MemoryExtractionService> logger,
        AgentMetrics? metrics = null)
    {
        this.llmClient = llmClient;
        this.embeddingService = embeddingService;
        this.consolidation = consolidation;
        this.metrics = metrics;
        this.logger = logger;
    }

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues a batch of raw messages for background memory extraction.
    /// Returns immediately; the batch will be processed by the single consumer loop.
    /// </summary>
    /// <returns><see langword="true"/> if enqueued, <see langword="false"/> if the queue is full.</returns>
    public bool EnqueueBatch(
        IReadOnlyList<ExtractionEntry> messages,
        string model,
        string conversationId)
    {
        if (messages.Count == 0) return true;

        var item = new ExtractionWorkItem
        {
            Messages = messages,
            Model = model,
            ConversationId = conversationId,
            EnqueuedAt = DateTimeOffset.UtcNow,
        };

        // Increment pending count BEFORE writing to the channel so that
        // WaitForIdleAsync cannot observe zero between enqueue and processing.
        Interlocked.Increment(ref this.pendingCount);

        // Reset the idle signal if it was previously completed
        lock (this.idleLock)
        {
            if (this.idleTcs.Task.IsCompleted)
            {
                this.idleTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (this.channel.Writer.TryWrite(item))
        {
            this.metrics?.ObserveExtractionQueueDepth(this.channel.Reader.Count);
            this.LogEnqueued(conversationId, this.channel.Reader.Count);
            return true;
        }

        // BoundedChannelFullMode.DropOldest should make TryWrite always succeed,
        // but just in case:
        Interlocked.Decrement(ref this.pendingCount);
        this.LogQueueFull(conversationId);
        return false;
    }

    /// <summary>
    /// Waits until all enqueued items have been processed (the queue is idle).
    /// Used after response delivery to ensure memory state is consistent
    /// before the next message is processed.
    /// </summary>
    /// <param name="timeout">Maximum time to wait. Throws <see cref="TimeoutException"/> if exceeded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task idleTask;
        lock (this.idleLock)
        {
            idleTask = this.idleTcs.Task;
        }

        // If already idle (nothing pending), return immediately
        if (Volatile.Read(ref this.pendingCount) <= 0)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await idleTask.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"MemoryExtractionService did not become idle within {timeout.TotalSeconds:F0}s. " +
                $"Pending items: {Volatile.Read(ref this.pendingCount)}");
        }
    }

    // ── Background consumer loop ─────────────────────────────────────────

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this.LogConsumerStarted();

        await foreach (var item in this.channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            this.metrics?.ObserveExtractionQueueDepth(this.channel.Reader.Count);
            var waitTime = DateTimeOffset.UtcNow - item.EnqueuedAt;
            this.LogProcessingStarted(item.ConversationId, waitTime.TotalSeconds);

            try
            {
                await ProcessMessagesAsync(
                    item.Messages,
                    item.Model,
                    item.ConversationId,
                    stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Consumer loop must not crash
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                this.LogConsumerStopping();
                break;
            }
            catch (Exception ex)
            {
                this.LogProcessingFailed(item.ConversationId, ex.Message);
            }
#pragma warning restore CA1031
            finally
            {
                // Signal idle when all enqueued items have been processed.
                if (Interlocked.Decrement(ref this.pendingCount) <= 0)
                {
                    lock (this.idleLock)
                    {
                        this.idleTcs.TrySetResult();
                    }
                }
            }
        }

        this.LogConsumerStopped(this.channel.Reader.Count);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Signal the writer that no more items will arrive, then let the base
        // class cancel ExecuteAsync's stoppingToken so the consumer drains.
        this.channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Core pipeline ────────────────────────────────────────────────────

    /// <summary>
    /// Extracts facts from a message window and consolidates them with existing memories.
    /// Called sequentially from the consumer loop.
    /// </summary>
    private async Task ProcessMessagesAsync(
        IReadOnlyList<ExtractionEntry> messages,
        string model,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var availabilityError = await MemoryFormatHelper.CheckEmbeddingAvailabilityAsync(this.embeddingService, cancellationToken).ConfigureAwait(false);
        if (availabilityError is not null)
        {
            this.LogEmbeddingUnavailable();
            return;
        }

        var chunks = ChunkEntries(messages);

        await this.consolidation.AcquireConsolidationLockAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batchMemories = new List<MemoryConsolidationService.BatchMemoryEntry>();

            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks.Count > 1)
                {
                    this.LogProcessingChunk(conversationId, i + 1, chunks.Count, chunks[i].Count);
                }

                var facts = await ExtractFactsAsync(chunks[i], model, cancellationToken)
                    .ConfigureAwait(false);

                if (facts.Count == 0)
                {
                    continue;
                }

                var titles = string.Join(", ", facts.Select(f => f.Title ?? "(untitled)"));
                this.LogFactsExtracted(conversationId, facts.Count, titles);

                foreach (var fact in facts)
                {
                    var consolidationFact = new MemoryConsolidationService.ConsolidationFact
                    {
                        Content = fact.Content,
                        Title = fact.Title,
                        Tags = fact.Tags,
                    };
                    await this.consolidation.ConsolidateAsync(consolidationFact, model, conversationId, batchMemories, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            this.consolidation.ReleaseConsolidationLock();
        }

        this.LogConsolidationComplete(conversationId, messages.Count);
    }

    /// <summary>
    /// Splits a list of extraction entries into chunks that fit within the
    /// character budget. Consecutive chunks overlap by <see cref="ExtractionChunkOverlapEntries"/>
    /// entries (2 user+assistant exchanges) so facts at boundaries aren't lost.
    /// </summary>
    internal static List<IReadOnlyList<ExtractionEntry>> ChunkEntries(IReadOnlyList<ExtractionEntry> entries)
    {
        if (entries.Count == 0)
        {
            return [entries];
        }

        var totalChars = 0;
        foreach (var entry in entries)
        {
            totalChars += entry.Content.Length;
        }

        if (totalChars <= ExtractionChunkSizeChars)
        {
            return [entries];
        }

        var chunks = new List<IReadOnlyList<ExtractionEntry>>();
        var startIdx = 0;

        while (startIdx < entries.Count)
        {
            var chunkChars = 0;
            var endIdx = startIdx;

            while (endIdx < entries.Count)
            {
                var entryChars = entries[endIdx].Content.Length;
                if (chunkChars + entryChars > ExtractionChunkSizeChars && endIdx > startIdx)
                {
                    break;
                }

                chunkChars += entryChars;
                endIdx++;
            }

            chunks.Add(entries.Skip(startIdx).Take(endIdx - startIdx).ToList());

            var nextStart = endIdx - ExtractionChunkOverlapEntries;
            if (nextStart <= startIdx)
            {
                nextStart = endIdx;
            }

            startIdx = nextStart;
        }

        return chunks;
    }

    /// <summary>
    /// Phase 1: Asks the LLM to extract discrete facts worth remembering from a
    /// message window.
    /// If the first response isn't valid JSON, sends a short follow-up asking
    /// "any facts? true/false" to cheaply distinguish "nothing to remember" from
    /// a formatting mistake before retrying.
    /// </summary>
    private async Task<List<ExtractedFact>> ExtractFactsAsync(
        IReadOnlyList<ExtractionEntry> messages,
        string model,
        CancellationToken cancellationToken)
    {
        var systemPromptBuilder = new System.Text.StringBuilder();

        systemPromptBuilder.Append("""
            You are a memory extraction system. Your job is to identify discrete, important facts
            from a conversation between a user and an AI assistant that are worth remembering long-term.

            Extract facts that fall into these categories:
            - User preferences, habits, or personal information
            - Decisions made or conclusions reached
            - Important context about projects, tasks, or goals
            - Relationships, names, locations, or other entities
            - Technical preferences or configuration choices

            Do NOT extract:
            - Trivial pleasantries or greetings
            - Transient information (e.g. "it's raining today")
            - Information the assistant generated that isn't grounded in the user's input
            - Duplicate variations of the same fact
            - Facts that are too vague or generic to be useful without the original conversation

            CRITICAL — self-contained context rule:
            Each fact will be stored and retrieved INDEPENDENTLY, without the original conversation.
            A fact that says "the process takes 2 years" is USELESS — which process?
            Every fact MUST name the specific subject, entity, or topic it refers to.
            Before writing a fact, ask: "Would someone reading ONLY this sentence understand
            what it's about?" If not, add the missing context.

            For each fact, provide:
            - "content": The fact as a clear, FULLY CONTEXTUALIZED statement that is meaningful
              on its own. Always include WHO/WHAT the fact is about. Never use pronouns like
              "it", "the process", "that thing" without specifying the referent.
            - "title": A short 3-5 word label
            - "tags": 1-3 categorization tags

            Respond with a JSON array. If there is nothing worth remembering, respond with an empty array [].
            Example:
            [
              {"content": "User's name is Yury and he lives in Copenhagen", "title": "User identity", "tags": ["personal", "location"]},
              {"content": "User prefers Claude over GPT for coding tasks", "title": "LLM preference", "tags": ["preferences", "technical"]}
            ]

            BAD examples (DO NOT produce facts like these):
            - "User mentioned that a certain process can take more than 2 years" (what process?)
            - "The application was submitted 1 year and 5 months ago" (what application?)
            - "User decided not to set a reminder for it" (for what?)

            GOOD versions of the same facts:
            - "Danish citizenship application processing can take more than 2 years"
            - "User submitted a Danish citizenship application approximately 1 year and 5 months ago"
            - "User declined to set a reminder for the Danish citizenship application"

            Respond with exactly one JSON array and nothing else — no commentary, no explanation, no markdown.
            """);

        var systemPrompt = systemPromptBuilder.ToString();

        var conversationBuilder = new System.Text.StringBuilder();
        conversationBuilder.AppendLine("Conversation to analyze:");
        conversationBuilder.AppendLine();
        foreach (var entry in messages)
        {
            var roleLabel = entry.Role == "user" ? "User" : "Assistant";
            var sanitized = ConversationPreprocessor.SanitizeForLlm(entry.Content);
            conversationBuilder.AppendLine(CultureInfo.InvariantCulture, $"{roleLabel}: {sanitized}");
            conversationBuilder.AppendLine();
        }

        var userPrompt = conversationBuilder.ToString();

        var llmMessages = new List<LlmMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt },
        };

        var request = new LlmCompletionRequest
        {
            Model = model,
            Messages = llmMessages,
            Temperature = 0.0,
            MaxTokens = TokenLimits.Medium,
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "memory-extraction",
        };

        var result = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Content))
        {
            this.LogLlmCallFailed("extraction", result.ErrorMessage ?? "empty response");
            return [];
        }

        // Happy path: valid JSON array
        var facts = ParseExtractedFacts(result.Content);
        if (facts is not null)
        {
            return facts;
        }

        // LLM returned prose. Ask a cheap follow-up: "any facts? true or false"
        this.LogNonJsonResponse(result.Content);

        llmMessages.Add(new LlmMessage { Role = "assistant", Content = result.Content });
        llmMessages.Add(new LlmMessage
        {
            Role = "user",
            Content = "Are there any facts worth remembering in that conversation? Answer with just true or false.",
        });

        var followUp = new LlmCompletionRequest
        {
            Model = model,
            Messages = llmMessages,
            Temperature = 0.0,
            MaxTokens = TokenLimits.Tiny,
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "memory-extraction",
        };

        var boolResult = await this.llmClient.CompleteAsync(followUp, cancellationToken).ConfigureAwait(false);

        if (!boolResult.Success || string.IsNullOrWhiteSpace(boolResult.Content))
        {
            return [];
        }

        var answer = boolResult.Content.Trim();

        // "false" / "no" → nothing to remember
        if (answer.StartsWith("false", StringComparison.OrdinalIgnoreCase)
            || answer.StartsWith("no", StringComparison.OrdinalIgnoreCase))
        {
            this.LogNoFactsConfirmed();
            return [];
        }

        // "true" / "yes" → retry extraction with a nudge for valid JSON
        llmMessages.Add(new LlmMessage { Role = "assistant", Content = answer });
        llmMessages.Add(new LlmMessage
        {
            Role = "user",
            Content = "OK, please provide them now as the JSON array as originally instructed. Respond with exactly one JSON array and nothing else.",
        });

        var retry = new LlmCompletionRequest
        {
            Model = model,
            Messages = llmMessages,
            Temperature = 0.0,
            MaxTokens = TokenLimits.Medium,
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = "memory-extraction",
        };

        var retryResult = await this.llmClient.CompleteAsync(retry, cancellationToken).ConfigureAwait(false);

        if (!retryResult.Success || string.IsNullOrWhiteSpace(retryResult.Content))
        {
            return [];
        }

        var retryFacts = ParseExtractedFacts(retryResult.Content);
        if (retryFacts is null)
        {
            this.LogExtractionRetryFailed(retryResult.Content);
            return [];
        }

        return retryFacts;
    }

    /// <summary>
    /// Attempts to parse the LLM's extraction response into a list of facts.
    /// Returns <see langword="null"/> if the response is not valid JSON (prose/refusal),
    /// or an empty list if it parsed but contained no facts.
    /// </summary>
    private static List<ExtractedFact>? ParseExtractedFacts(string llmResponse)
    {
        var json = MemoryConsolidationService.StripToJson(llmResponse);

        // Not JSON at all — return null so caller can retry
        if (json.Length == 0 || (json[0] != '[' && json[0] != '{'))
        {
            return null;
        }

        try
        {
            var facts = JsonSerializer.Deserialize<List<ExtractedFact>>(json, JsonOptions);
            if (facts is null)
            {
                return [];
            }

            // Safety limit
            if (facts.Count > MaxFactsPerExtraction)
            {
                facts = facts.GetRange(0, MaxFactsPerExtraction);
            }

            // Filter out empty facts
            return facts.Where(f => !string.IsNullOrWhiteSpace(f.Content)).ToList();
        }
        catch (JsonException)
        {
            // Looked like JSON but wasn't valid — return null so caller can retry
            return null;
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    private sealed record ExtractedFact
    {
        public string Content { get; init; } = string.Empty;
        public string? Title { get; init; }
        public List<string>? Tags { get; init; }
    }

    private sealed record ExtractionWorkItem
    {
        public required IReadOnlyList<ExtractionEntry> Messages { get; init; }
        public required string Model { get; init; }
        public required string ConversationId { get; init; }
        public required DateTimeOffset EnqueuedAt { get; init; }
    }

    // ── LoggerMessage source-generated methods ──────────────────────────

    // Consumer lifecycle
    [LoggerMessage(Level = LogLevel.Information, Message = "Memory extraction consumer started (queue capacity={MaxQueueSize})")]
    private static partial void LogConsumerStartedStatic(ILogger logger, int maxQueueSize);

    private void LogConsumerStarted() => LogConsumerStartedStatic(logger, MaxQueueSize);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory extraction consumer stopping (cancellation requested)")]
    private partial void LogConsumerStopping();

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory extraction consumer stopped, {RemainingItems} items left in queue")]
    private partial void LogConsumerStopped(int remainingItems);

    // Enqueue
    [LoggerMessage(Level = LogLevel.Debug, Message = "[{ConversationId}] Enqueued for memory extraction (queue depth: {QueueDepth})")]
    private partial void LogEnqueued(string conversationId, int queueDepth);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ConversationId}] Memory extraction queue full, message pair dropped")]
    private partial void LogQueueFull(string conversationId);

    // Processing
    [LoggerMessage(Level = LogLevel.Debug, Message = "[{ConversationId}] Processing message pair (waited {WaitSeconds:F1}s in queue)")]
    private partial void LogProcessingStarted(string conversationId, double waitSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Processing extraction chunk {ChunkIndex}/{TotalChunks} ({EntryCount} entries)")]
    private partial void LogProcessingChunk(string conversationId, int chunkIndex, int totalChunks, int entryCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[{ConversationId}] Memory extraction processing failed: {ErrorMessage}")]
    private partial void LogProcessingFailed(string conversationId, string errorMessage);

    // Extraction phase
    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory extraction skipped: embedding service unavailable")]
    private partial void LogEmbeddingUnavailable();

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Extracted {Count} facts: [{Titles}]")]
    private partial void LogFactsExtracted(string conversationId, int count, string titles);

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory consolidation complete for {Count} facts")]
    private partial void LogConsolidationComplete(string conversationId, int count);

    // LLM / parsing
    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM call failed during {Phase}: {ErrorMessage}")]
    private partial void LogLlmCallFailed(string phase, string errorMessage);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extraction LLM returned prose instead of JSON, asking follow-up. Full response: {LlmResponse}")]
    private partial void LogNonJsonResponse(string llmResponse);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM confirmed no facts worth remembering")]
    private partial void LogNoFactsConfirmed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Extraction retry also returned non-JSON. Full response: {LlmResponse}")]
    private partial void LogExtractionRetryFailed(string llmResponse);
}
