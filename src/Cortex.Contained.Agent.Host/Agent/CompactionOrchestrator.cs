using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Owns the conversation-compaction cluster extracted from
/// <see cref="AgentRuntime"/>: the proactive/manual summarization
/// (<see cref="CompactConversationAsync"/>), the context-overflow
/// <see cref="EmergencyCompactAsync"/>, the <see cref="ShouldCompact"/>
/// token-budget check, and the extraction-buffer flush that precedes every
/// compaction (<see cref="FlushExtractionBuffer"/>).
/// <para>
/// Move-only refactor: semantics and call order are preserved exactly. The pure
/// split/wrap/threshold helpers stay as <c>internal static</c> on
/// <see cref="AgentRuntime"/> (referenced by unit tests) and are invoked here.
/// </para>
/// </summary>
internal sealed partial class CompactionOrchestrator
{
    private readonly ILlmClient llmClient;
    private readonly IModelProvider modelProvider;
    private readonly Memory.MemoryExtractionService? memoryExtraction;
    private readonly IOptionsMonitor<ConversationCompactionConfig>? compactionOptions;
    private readonly IOptionsMonitor<ImageAgingConfig> imageAgingOptions;
    private readonly IImageDescriber? imageDescriber;
    private readonly ILogger<CompactionOrchestrator> logger;

    /// <summary>
    /// Fallback context window when the model's actual limit is unknown.
    /// </summary>
    private const int FallbackContextWindow = 128_000;

    /// <summary>
    /// Compaction triggers when prompt tokens reach this fraction of the context window.
    /// Lowered from 0.80 to 0.65 to compact earlier, keeping per-round costs lower.
    /// </summary>
    internal const double CompactionThreshold = 0.65;

    /// <summary>
    /// Prompt for the compaction LLM call. Asks the model to produce a structured
    /// summary of the conversation so far, following a template inspired by
    /// OpenCode's compaction format.
    /// </summary>
    private const string CompactionSystemPrompt = """
        You are a conversation summarizer. Produce a concise but comprehensive summary
        of the conversation below. The summary will replace the original messages in
        the context window so the assistant can continue the conversation without losing
        important context.

        Use this exact structure:

        ## Goal
        What the user is trying to accomplish (1-2 sentences).

        ## Instructions and preferences
        Key instructions, preferences, or constraints the user has stated.

        ## Discoveries and decisions
        Notable things learned or decided during the conversation.

        ## Completed actions
        What has been done — in past tense ("sent email to X", "added calendar event Y",
        "looked up Z"). These are a record of actions already taken, not pending work.

        ## Most recent exchange
        What the user said most recently and how the assistant responded (1-3 sentences).
        Include direct quotes of any specific ask or detail.

        ## Next step
        The specific next action based on the most recent exchange. If the last task
        was concluded, write "Awaiting next user request" and do not reintroduce
        tangential or already-completed requests.

        ## Relevant references
        File paths, URLs, names, identifiers, skills used (e.g. gmail, food-tracker),
        memory IDs referenced, or other concrete values that were discussed.

        Be factual and specific. Include concrete values (names, dates, places, decisions,
        content the user cared about) — don't paraphrase what the user actually said.
        Do not include greetings, filler, or meta-commentary about the summarization itself.
        """;

    public CompactionOrchestrator(
        ILlmClient llmClient,
        IModelProvider modelProvider,
        IOptionsMonitor<ImageAgingConfig> imageAgingOptions,
        ILogger<CompactionOrchestrator> logger,
        Memory.MemoryExtractionService? memoryExtraction = null,
        IOptionsMonitor<ConversationCompactionConfig>? compactionOptions = null,
        IImageDescriber? imageDescriber = null)
    {
        this.llmClient = llmClient;
        this.modelProvider = modelProvider;
        this.imageAgingOptions = imageAgingOptions;
        this.logger = logger;
        this.memoryExtraction = memoryExtraction;
        this.compactionOptions = compactionOptions;
        this.imageDescriber = imageDescriber;
    }

    private string DefaultModel => this.modelProvider.DefaultModel;

    /// <summary>Whether a background memory-extraction pipeline is wired up.</summary>
    public bool HasMemoryExtraction => this.memoryExtraction is not null;

    /// <summary>
    /// Flushes the extraction buffer to the background extraction service.
    /// Called only before compaction — during active conversation all
    /// information is already present in the context window.
    /// </summary>
    public void FlushExtractionBuffer(AgentSession session, string conversationId)
    {
        var entries = session.DrainExtractionBuffer();
        if (entries.Count == 0)
        {
            return;
        }

        this.memoryExtraction!.EnqueueBatch(
            entries,
            this.modelProvider.MemoryModel,
            conversationId);

        this.LogExtractionFlushed(conversationId, entries.Count);
    }

    /// <summary>
    /// Waits for the background extraction pipeline to drain. No-op when no
    /// extraction pipeline is wired up.
    /// </summary>
    public ValueTask WaitForExtractionIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (this.memoryExtraction is null)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(this.memoryExtraction.WaitForIdleAsync(timeout, cancellationToken));
    }

    /// <summary>
    /// Determines whether compaction should be triggered based on actual token usage.
    /// </summary>
    public bool ShouldCompact(AgentSession session)
    {
        if (session.LastPromptTokens <= 0)
        {
            return false;
        }

        var contextWindow = this.modelProvider.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var threshold = (int)(contextWindow * CompactionThreshold);
        return session.LastPromptTokens >= threshold;
    }

    /// <summary>
    /// Compacts the conversation by summarizing ALL conversation messages into a single
    /// summary. Following OpenCode's approach: the summary completely replaces the prior
    /// conversation — no raw messages are kept. This is more aggressive than keeping a
    /// percentage, but ensures maximum token savings and lets the summary carry all context.
    /// </summary>
    public async Task CompactConversationAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var history = session.GetHistory();
        if (history.Count < 6)
        {
            return; // Too few messages to compact
        }

        // Skip system messages — BuildPrompt creates them fresh each time.
        var systemCount = history.TakeWhile(m => m.Role == "system").Count();
        var conversationMessages = history.Skip(systemCount).ToList();

        if (conversationMessages.Count < 4)
        {
            return; // Need enough conversation to summarize
        }

        // Tail-aware compaction: prefer to preserve the most recent N user
        // turns verbatim (so freshly-attached images, recent reasoning, etc.
        // survive a summarization pass) when their combined size fits in a
        // budget. Falls back to tool-round preservation, then to summarizing
        // everything, depending on shape and size.
        var contextWindowForBudget = this.modelProvider.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var compactionConfig = this.compactionOptions?.CurrentValue ?? new ConversationCompactionConfig();
        var preserveBudgetTokens = (int)(contextWindowForBudget * compactionConfig.PreserveBudgetRatio);
        var (messagesToSummarize, preservedTail) = AgentRuntime.SplitPreservingRecentTurns(
            conversationMessages,
            compactionConfig.PreserveRecentTurns,
            preserveBudgetTokens);

        // Build the conversation text for summarization
        var conversationText = new StringBuilder();
        foreach (var msg in messagesToSummarize)
        {
            var role = msg.Role.ToUpperInvariant();
            if (msg.ToolCalls is { Count: > 0 })
            {
                conversationText.AppendLine(CultureInfo.InvariantCulture, $"[{role}] (tool calls: {string.Join(", ", msg.ToolCalls.Select(tc => tc.Name))})");
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    conversationText.AppendLine(msg.Content);
                }
            }
            else if (msg.Role == "tool")
            {
                // Truncate long tool results for the summary prompt
                var content = msg.Content ?? string.Empty;
                if (content.Length > 500)
                {
                    content = string.Concat(content.AsSpan(0, 500), "... [truncated]");
                }
                conversationText.AppendLine(CultureInfo.InvariantCulture, $"[TOOL RESULT] {content}");
            }
            else
            {
                conversationText.AppendLine(CultureInfo.InvariantCulture, $"[{role}] {msg.Content}");
            }
        }

        // Cap the conversation text to avoid exceeding the context window for the
        // compaction call itself. Use half the context window as a safe limit.
        var contextWindow = this.modelProvider.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var maxCompactionChars = (int)(contextWindow * 1.5); // ~1.5 chars/token is conservative for this estimate
        var conversationString = conversationText.ToString();
        if (conversationString.Length > maxCompactionChars)
        {
            conversationString = string.Concat(
                "[Earlier conversation truncated]\n\n",
                conversationString.AsSpan(conversationString.Length - maxCompactionChars));
        }

        this.LogCompactionStarting(session.ConversationId, messagesToSummarize.Count, session.LastPromptTokens);

        // Call the LLM to generate a summary
        var summaryRequest = new LlmCompletionRequest
        {
            Model = this.DefaultModel,
            Messages =
            [
                new LlmMessage { Role = "system", Content = CompactionSystemPrompt },
                new LlmMessage { Role = "user", Content = conversationString },
            ],
            MaxTokens = TokenLimits.ResolveMaxOutput(this.modelProvider),
            RequestId = $"compact-{Guid.NewGuid():N}",
            ConversationId = session.ConversationId,
        };

        try
        {
            var result = await this.llmClient.CompleteAsync(summaryRequest, cancellationToken).ConfigureAwait(false);

            if (!result.Success || string.IsNullOrEmpty(result.Content))
            {
                this.LogCompactionFailed(session.ConversationId, result.ErrorMessage ?? "Empty summary");
                return;
            }

            // Replace the summarized portion with a single wrapped-summary user turn.
            // System messages are NOT preserved — BuildPrompt creates them fresh.
            // Case A (at rest): conversation ends with [user: wrapped-summary].
            // Case B (mid-tool-loop): preserved tail (assistant(tool_calls) + tool results)
            // follows the summary verbatim, so the model can continue the tool loop.
            // No re-injection of the original last user message — that caused the
            // side-effect replay bug (a completed-action request treated as pending).
            var summaryUserMessage = new LlmMessage
            {
                Role = "user",
                Content = AgentRuntime.WrapSummaryForContinuation(result.Content, hasTail: preservedTail.Count > 0),
                MessageType = LlmMessageType.CompactionSummary,
            };

            session.ClearHistory();
            session.AddMessage(summaryUserMessage);
            foreach (var msg in preservedTail)
            {
                session.AddMessage(msg);
            }

            this.LogCompactionCompleted(session.ConversationId, messagesToSummarize.Count, result.Content.Length);
            this.LogCompactionShape(session.ConversationId, preservedTail.Count > 0, preservedTail.Count);
        }
#pragma warning disable CA1031 // Compaction must not crash the main generation flow
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCompactionFailed(session.ConversationId, ex.Message);
        }
    }

    /// <summary>
    /// Emergency compaction triggered by a context overflow error from the LLM.
    /// Strips media (images) from all messages in the session history, truncates
    /// oversized tool results, and — only if the stripped history is still too
    /// large for the context window — runs a standard summarization compaction.
    /// <para>
    /// The conditional summarization is important: most 413 errors are HTTP body
    /// size failures driven by base64-encoded images, not token-count overflow.
    /// Stripping images alone typically shrinks the payload by several MB,
    /// bringing it comfortably under both HTTP and token limits. Running
    /// summarization on top would then collapse freshly-generated verbose image
    /// descriptions (and other recent detail the user is still asking about)
    /// into a short summary — exactly what the describer work was trying to
    /// avoid. Skip summarization when strip was enough.
    /// </para>
    /// </summary>
    public async Task EmergencyCompactAsync(AgentSession session, CancellationToken cancellationToken)
    {
        this.LogEmergencyCompactionStarting(session.ConversationId);
        this.LogEmergencyCompactionDescriber(session.ConversationId, this.imageDescriber is not null);

        // Step 1: Strip images and truncate large tool results in-place
        var history = session.GetHistory();
        var stripped = await ContextManager.StripMediaAsync(
            history,
            this.imageAgingOptions.CurrentValue,
            this.imageDescriber,
            cancellationToken).ConfigureAwait(false);
        session.ClearHistory();
        foreach (var msg in stripped)
        {
            session.AddMessage(msg);
        }

        // Step 2: Only summarize if stripping wasn't enough to get under the
        // normal compaction threshold.
        var contextWindow = this.modelProvider.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var strippedTokens = TokenEstimator.EstimateTokens(stripped);
        if (AgentRuntime.StripAloneSufficient(strippedTokens, contextWindow))
        {
            this.LogEmergencyCompactionStripSufficient(session.ConversationId, strippedTokens, (int)(contextWindow * CompactionThreshold));
            this.LogEmergencyCompactionCompleted(session.ConversationId, session.MessageCount);
            return;
        }

        await CompactConversationAsync(session, cancellationToken).ConfigureAwait(false);

        this.LogEmergencyCompactionCompleted(session.ConversationId, session.MessageCount);
    }

    // ── LoggerMessage source-generated methods ───────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "[{ConversationId}] Memory extraction batch flushed: {PairCount} entries")]
    private partial void LogExtractionFlushed(string conversationId, int pairCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compaction starting for {ConversationId}: summarizing {MessageCount} messages, promptTokens={PromptTokens}")]
    private partial void LogCompactionStarting(string conversationId, int messageCount, int promptTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compaction completed for {ConversationId}: summarized {MessageCount} messages into {SummaryLength} chars")]
    private partial void LogCompactionCompleted(string conversationId, int messageCount, int summaryLength);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compaction shape for {ConversationId}: tailPreserved={TailPreserved} (tailMessages={TailCount})")]
    private partial void LogCompactionShape(string conversationId, bool tailPreserved, int tailCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compaction failed for {ConversationId}: {ErrorMessage}")]
    private partial void LogCompactionFailed(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emergency compaction starting for {ConversationId}: stripping media and truncating large tool results")]
    private partial void LogEmergencyCompactionStarting(string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emergency compaction for {ConversationId}: image describer available={DescriberAvailable}")]
    private partial void LogEmergencyCompactionDescriber(string conversationId, bool describerAvailable);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emergency compaction for {ConversationId}: strip alone sufficient — skipping summarization (strippedTokens={StrippedTokens}, threshold={ThresholdTokens})")]
    private partial void LogEmergencyCompactionStripSufficient(string conversationId, int strippedTokens, int thresholdTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Emergency compaction completed for {ConversationId}: session now has {MessageCount} messages")]
    private partial void LogEmergencyCompactionCompleted(string conversationId, int messageCount);
}
