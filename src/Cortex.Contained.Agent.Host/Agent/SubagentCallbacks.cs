using System.Diagnostics;
using System.Globalization;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// <see cref="IAgentLoopCallbacks"/> implementation for subagent execution.
/// Handles: context preparation with todo injection, message persistence,
/// compaction via ContextManager, completion callbacks.
/// Does NOT handle: streaming to Bridge, memory extraction, topic detection.
/// </summary>
public sealed partial class SubagentCallbacks : IAgentLoopCallbacks
{
    private readonly List<LlmMessage> messages;
    private readonly int contextWindow;
    private readonly int maxOutputTokens;
    private readonly string conversationId;
    private readonly InMemoryTodoStore? todoStore;
    private readonly SubagentSessionStore? store;
    private readonly string? taskId;
    private readonly ILlmClient llmClient;
    private readonly ILogger logger;
    private readonly AgentSession? pendingSession;
    private readonly ImageAgingConfig imageAging;
    private readonly IImageDescriber? imageDescriber;

    /// <summary>Compaction threshold — compact when context reaches this fraction of the window.</summary>
    private const double CompactionThreshold = 0.65;

    /// <summary>Minimum messages required to attempt compaction.</summary>
    private const int MinMessagesForCompaction = 6;

    public SubagentCallbacks(
        List<LlmMessage> messages,
        int contextWindow,
        int maxOutputTokens,
        string conversationId,
        ILlmClient llmClient,
        ILogger logger,
        InMemoryTodoStore? todoStore = null,
        SubagentSessionStore? store = null,
        string? taskId = null,
        AgentSession? pendingSession = null,
        ImageAgingConfig? imageAging = null,
        IImageDescriber? imageDescriber = null)
    {
        this.messages = messages;
        this.contextWindow = contextWindow;
        this.maxOutputTokens = maxOutputTokens;
        this.conversationId = conversationId;
        this.llmClient = llmClient;
        this.logger = logger;
        this.todoStore = todoStore;
        this.store = store;
        this.taskId = taskId;
        this.pendingSession = pendingSession;
        this.imageAging = imageAging ?? new ImageAgingConfig();
        this.imageDescriber = imageDescriber;
    }

    public async Task<List<LlmMessage>> PrepareMessagesAsync(int round, CancellationToken cancellationToken)
    {
        // Check if context is too large BEFORE preparing — compact if needed.
        // This catches bloat from large tool results (e.g., file_read returning 50KB)
        // that the post-round compaction missed (it uses stale token counts).
        if (round > 0 && this.messages.Count >= MinMessagesForCompaction)
        {
            var estimatedTokens = TokenEstimator.EstimateTokens(this.messages);
            var threshold = (int)(this.contextWindow * CompactionThreshold);
            if (estimatedTokens > threshold)
            {
                this.LogCompactionTriggered(this.conversationId, estimatedTokens, threshold);
                await CompactAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var prepared = await ContextManager.PrepareMessagesAsync(
            this.messages,
            this.contextWindow,
            this.maxOutputTokens,
            this.imageAging,
            this.imageDescriber,
            cancellationToken).ConfigureAwait(false);

        // Inject current todo progress into system message
        if (this.todoStore is not null && prepared.Count > 0 && prepared[0].Role == "system")
        {
            var todos = this.todoStore.ReadAll(this.conversationId);
            if (todos.Count > 0)
            {
                var todoSection = "\n\n## Your current progress\n";
                foreach (var list in todos)
                {
                    todoSection += list.Markdown;
                }

                prepared[0] = prepared[0] with { Content = prepared[0].Content + todoSection };
            }
        }

        return prepared;
    }

    public void DrainInjectedMessages()
    {
        if (this.pendingSession is null)
        {
            return;
        }

        var pending = this.pendingSession.DrainPendingMessages();
        foreach (var msg in pending)
        {
            this.OnAssistantMessage(new LlmMessage { Role = "user", Content = msg.Text });
        }
    }

    public Task OnContentDeltaAsync(string delta, int sequenceNumber, CancellationToken ct)
    {
        // No streaming delivery for subagents
        return Task.CompletedTask;
    }

    public Task OnToolStartAsync(LlmToolCall toolCall, CancellationToken ct)
    {
        if (string.Equals(toolCall.Name, "run_command", StringComparison.OrdinalIgnoreCase))
        {
            this.LogToolExecutingWithArgs(this.conversationId, toolCall.Name, toolCall.Arguments);
        }
        else
        {
            this.LogToolExecuting(this.conversationId, toolCall.Name);
        }

        return Task.CompletedTask;
    }

    public Task OnToolCompleteAsync(LlmToolCall toolCall, AgentToolResult result, TimeSpan duration, CancellationToken ct)
    {
        this.LogToolCompleted(this.conversationId, toolCall.Name, result.Success, (long)duration.TotalMilliseconds);
        return Task.CompletedTask;
    }

    public async Task OnRoundCompleteAsync(int round, LlmTokenUsage? usage, CancellationToken ct)
    {
        // Persist messages to SubagentSessionStore after each round
        if (this.store is not null && this.taskId is not null)
        {
            this.store.UpdateMessages(this.taskId, this.messages, round);
        }

        // Check if compaction is needed
        if (usage is not null && this.messages.Count >= MinMessagesForCompaction)
        {
            var threshold = (int)(this.contextWindow * CompactionThreshold);
            if (usage.PromptTokens > threshold)
            {
                this.LogCompactionTriggered(this.conversationId, usage.PromptTokens, threshold);
                await CompactAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public Task<bool> OnContextOverflowAsync(string errorMessage, CancellationToken ct)
    {
        this.LogContextOverflow(this.conversationId, errorMessage);

        // Emergency compaction: remove old tool results aggressively
        if (this.messages.Count >= MinMessagesForCompaction)
        {
            // Keep system message + last few exchanges
            var systemMessages = this.messages.TakeWhile(m => m.Role == "system").ToList();
            var nonSystem = this.messages.Skip(systemMessages.Count).ToList();

            // Keep only the last 10 messages (5 exchanges)
            var keepCount = Math.Min(nonSystem.Count, 10);
            this.messages.Clear();
            this.messages.AddRange(systemMessages);
            this.messages.AddRange(nonSystem.TakeLast(keepCount));

            this.LogEmergencyCompaction(this.conversationId, this.messages.Count);
            return Task.FromResult(true); // retry
        }

        return Task.FromResult(false); // can't recover
    }

    public Task OnErrorAsync(string errorMessage, CancellationToken ct)
    {
        this.LogError(this.conversationId, errorMessage);
        return Task.CompletedTask;
    }

    public Task OnDoomLoopAsync(string toolName, CancellationToken ct)
    {
        this.LogDoomLoop(this.conversationId, toolName);
        return Task.CompletedTask;
    }

    public Task OnLoopCompleteAsync(AgentLoopResult result, CancellationToken ct)
    {
        // Persist final state
        if (this.store is not null && this.taskId is not null)
        {
            this.store.UpdateMessages(this.taskId, this.messages, result.RoundsExecuted);
        }

        return Task.CompletedTask;
    }

    public void OnAssistantMessage(LlmMessage message)
    {
        this.messages.Add(message);
    }

    public void OnToolResultMessage(LlmMessage message)
    {
        this.messages.Add(message);
    }

    // ── Private: Compaction ──────────────────────────────────────────────

    /// <summary>
    /// Compact the subagent's conversation by summarizing with an LLM call.
    /// Similar to AgentRuntime.CompactConversationAsync but simplified for subagents.
    /// </summary>
    private async Task CompactAsync(CancellationToken ct)
    {
        if (this.messages.Count < MinMessagesForCompaction)
        {
            return;
        }

        // Build a summary prompt from the conversation
        var historyText = new System.Text.StringBuilder();
        foreach (var msg in this.messages)
        {
            if (msg.Role == "system")
            {
                continue;
            }

            var role = msg.Role == "assistant" ? "Assistant" : msg.Role == "tool" ? "Tool" : "User";
            var content = msg.Content ?? "(tool call)";
            if (content.Length > 500)
            {
                content = string.Concat(content.AsSpan(0, 497), "...");
            }
            historyText.AppendLine(CultureInfo.InvariantCulture, $"{role}: {content}");
        }

        var summaryRequest = new LlmCompletionRequest
        {
            Model = this.messages.FirstOrDefault(m => m.Role == "system")?.Content?.Length > 0
                ? "gpt-4o-mini" // use cheap model for compaction
                : "gpt-4o-mini",
            Messages =
            [
                new LlmMessage
                {
                    Role = "system",
                    Content = "Summarize this conversation concisely. Preserve: what was accomplished, " +
                              "key findings, current state, and what remains to be done. Be specific about " +
                              "results (numbers, names, URLs). Omit tool call details and intermediate steps.",
                },
                new LlmMessage { Role = "user", Content = historyText.ToString() },
            ],
            MaxTokens = this.maxOutputTokens,
            Temperature = 0.0,
            RequestId = Guid.NewGuid().ToString("N"),
            ConversationId = $"{this.conversationId}-compact",
        };

        try
        {
            var result = await this.llmClient.CompleteAsync(summaryRequest, ct).ConfigureAwait(false);
            if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
            {
                // Keep system message, replace everything else with summary
                var systemMsg = this.messages.FirstOrDefault(m => m.Role == "system");
                this.messages.Clear();
                if (systemMsg is not null)
                {
                    this.messages.Add(systemMsg);
                }
                this.messages.Add(new LlmMessage
                {
                    Role = "user",
                    Content = $"[Conversation summary — continue from here]\n{result.Content}",
                });

                this.LogCompactionCompleted(this.conversationId, this.messages.Count);
            }
        }
#pragma warning disable CA1031 // Compaction failure should not crash the subagent
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.LogCompactionFailed(this.conversationId, ex.Message);
        }
    }

    // ── Logging ──────────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent] {ConversationId} executing {ToolName}")]
    private partial void LogToolExecuting(string conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent] {ConversationId} executing {ToolName}: {Arguments}")]
    private partial void LogToolExecutingWithArgs(string conversationId, string toolName, string arguments);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent] {ConversationId} {ToolName} completed: success={Success} ({ElapsedMs}ms)")]
    private partial void LogToolCompleted(string conversationId, string toolName, bool success, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent] {ConversationId} error: {ErrorMessage}")]
    private partial void LogError(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent] {ConversationId} doom loop detected: tool '{ToolName}'")]
    private partial void LogDoomLoop(string conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent] {ConversationId} context overflow: {ErrorMessage}")]
    private partial void LogContextOverflow(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent] {ConversationId} emergency compaction: {MessageCount} messages remaining")]
    private partial void LogEmergencyCompaction(string conversationId, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent] {ConversationId} compaction triggered: {PromptTokens} tokens > {Threshold} threshold")]
    private partial void LogCompactionTriggered(string conversationId, int promptTokens, int threshold);

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent] {ConversationId} compaction completed: {MessageCount} messages")]
    private partial void LogCompactionCompleted(string conversationId, int messageCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent] {ConversationId} compaction failed: {ErrorMessage}")]
    private partial void LogCompactionFailed(string conversationId, string errorMessage);
}
