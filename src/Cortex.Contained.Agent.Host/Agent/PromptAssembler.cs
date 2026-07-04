using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Cortex.Contained.Contracts.SystemPrompt;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Assembles the per-turn LLM message list for the main agent: builds the
/// system prompt (personality + self-notes + skills + channel/voice context +
/// operational state) and appends the conversation history, then prunes/trims
/// it to fit the context window via <see cref="ContextManager"/>.
/// <para>
/// Extracted from <see cref="AgentRuntime"/> as a move-only refactor; semantics
/// and call order are preserved exactly. Personality is read through a delegate
/// supplied by <see cref="AgentRuntime"/> (which still owns personality file
/// read/write to back the public personality API), avoiding a back-reference.
/// </para>
/// </summary>
internal sealed partial class PromptAssembler
{
    private readonly Func<string> loadPersonality;
    private readonly SelfNotesStore? selfNotesStore;
    private readonly SkillRegistry? skillRegistry;
    private readonly SubagentSessionStore? subagentStore;
    private readonly TodoStoreResolver? todoResolver;
    private readonly IModelProvider modelProvider;
    private readonly IOptionsMonitor<ImageAgingConfig> imageAgingOptions;
    private readonly IImageDescriber? imageDescriber;
    private readonly SystemPromptStore? systemPromptStore;
    private readonly ILogger<PromptAssembler> logger;

    /// <summary>
    /// Fallback context window when the model's actual limit is unknown.
    /// </summary>
    private const int FallbackContextWindow = 128_000;

    public PromptAssembler(
        Func<string> loadPersonality,
        IModelProvider modelProvider,
        IOptionsMonitor<ImageAgingConfig> imageAgingOptions,
        ILogger<PromptAssembler> logger,
        SelfNotesStore? selfNotesStore = null,
        SkillRegistry? skillRegistry = null,
        SubagentSessionStore? subagentStore = null,
        TodoStoreResolver? todoResolver = null,
        IImageDescriber? imageDescriber = null,
        SystemPromptStore? systemPromptStore = null)
    {
        this.loadPersonality = loadPersonality;
        this.modelProvider = modelProvider;
        this.imageAgingOptions = imageAgingOptions;
        this.logger = logger;
        this.selfNotesStore = selfNotesStore;
        this.skillRegistry = skillRegistry;
        this.subagentStore = subagentStore;
        this.todoResolver = todoResolver;
        this.imageDescriber = imageDescriber;
        this.systemPromptStore = systemPromptStore;
    }

    /// <summary>
    /// Builds the system prompt + conversation history message list for an LLM
    /// turn, then prunes/strips/trims it to fit the context window.
    /// </summary>
    public async Task<List<LlmMessage>> BuildPromptAsync(AgentSession session, CancellationToken ct, string? channelId = null, bool isVoice = false)
    {
        // Part 1: Personality (who I am — set by user)
        var personality = this.loadPersonality();

        // Part 2: Self-notes (how I work — set by agent via self_notes_write tool)
        var selfNotes = this.selfNotesStore?.Read() ?? string.Empty;

        // Part 3: Skills (specialized workflows discoverable via file_read)
        var skillsSection = this.skillRegistry?.FormatForSystemPrompt() ?? string.Empty;

        // Channel context (tiny, tells agent where the conversation is)
        var channelLabel = GetChannelLabel(channelId);
        var channelValue = channelLabel is not null
            ? $"\nThe user is currently talking to you via {channelLabel}."
            : string.Empty;

        // Voice-enrollment is opt-in only — entry points are an explicit user
        // request (the LLM sees start_voice_enrollment in its tool list),
        // the /voice-id enroll slash command, or the Web UI "Start
        // enrollment" button. No proactive prompts from the agent.

        var activeTasksValue = this.BuildActiveTasksSection();
        var activePlansValue = this.BuildActivePlansSection(session);

        var config = this.systemPromptStore?.Read() ?? SystemPromptDefaults.Create();

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["personality"] = personality,
            ["self_notes"] = selfNotes,
            ["skills"] = skillsSection,
            ["channel"] = channelValue,
            ["voice_mode"] = isVoice ? config.VoiceMode : string.Empty,
            ["active_tasks"] = activeTasksValue,
            ["active_plans"] = activePlansValue,
            ["coding_relay"] = config.CodingRelay,
        };

        var systemPrompt = SystemPromptRenderer.Render(config.MainTemplate, values);

        if (this.logger.IsEnabled(LogLevel.Debug))
        {
            var fingerprint = this.systemPromptStore?.Fingerprint() ?? "default";
            this.LogPromptFingerprint(session.ConversationId, fingerprint);
        }

        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = systemPrompt },
        };

        // Add conversation history
        foreach (var msg in session.GetHistory())
        {
            messages.Add(msg);
        }

        // Prune old tool results, strip media, and trim to fit within context window
        var contextWindow = this.modelProvider.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var reserveForResponse = TokenLimits.ResolveMaxOutput(this.modelProvider);
        var historyCount = messages.Count - 1; // Exclude the system message we just added
        var estimatedTokens = TokenEstimator.EstimateTokens(messages);
        var prepared = await ContextManager.PrepareMessagesAsync(
            messages,
            contextWindow,
            reserveForResponse,
            this.imageAgingOptions.CurrentValue,
            this.imageDescriber,
            ct).ConfigureAwait(false);
        var preparedTokens = TokenEstimator.EstimateTokens(prepared);

        this.LogBuildPrompt(session.ConversationId, historyCount, estimatedTokens, prepared.Count - 1, preparedTokens, contextWindow, reserveForResponse);

        // Diagnostic: log all message roles to trace "must end with user message" errors
        if (prepared.Count > 0)
        {
            var allRoles = string.Join(" → ", prepared.Select(m =>
                m.Role + (m.ToolCalls is { Count: > 0 } ? $"(+{m.ToolCalls.Count}tools)" : "")));
            this.LogPromptTail(session.ConversationId, allRoles, prepared[^1].Role);
        }

        // Log diagnostic info when trimming drops all conversation messages
        var preparedConversationCount = prepared.Count(m => m.Role != "system");
        if (preparedConversationCount == 0 && historyCount > 0)
        {
            this.LogContextOverflow(session.ConversationId, historyCount, estimatedTokens, contextWindow, reserveForResponse);
        }
        else if (prepared.Count < messages.Count)
        {
            var droppedCount = messages.Count - prepared.Count;
            this.LogContextTrimmed(session.ConversationId, droppedCount, historyCount, estimatedTokens, contextWindow);
        }

        return prepared;
    }

    /// <summary>
    /// Maps a canonical channel ID to a human-readable label for the system prompt.
    /// Returns null for unknown or synthetic channels (e.g. "scheduled").
    /// </summary>
    private static string? GetChannelLabel(string? channelId) =>
        Tools.ChannelCatalog.ByCanonicalId(channelId)?.PromptLabel;

    /// <summary>
    /// Builds the "Active background tasks" section (operational state) or an empty
    /// string when there are none / no subagent store is configured.
    /// </summary>
    private string BuildActiveTasksSection()
    {
        if (this.subagentStore is null)
        {
            return string.Empty;
        }

        var activeTasks = this.subagentStore.GetActive();
        if (activeTasks.Count == 0)
        {
            return string.Empty;
        }

        var section = "\n\n## Active background tasks\n";
        foreach (var task in activeTasks)
        {
            var elapsed = (DateTimeOffset.UtcNow - task.CreatedAt).TotalMinutes;
            var stateLabel = task.State.ToStorageValue();
            section += $"- [{task.TaskId}] \"{task.Description}\" ({stateLabel}, {elapsed:F0}m ago)\n";
        }

        return section;
    }

    /// <summary>
    /// Builds the "Active plans" section (operational state) or an empty string when
    /// there are none / no todo resolver is configured.
    /// </summary>
    private string BuildActivePlansSection(AgentSession session)
    {
        if (this.todoResolver is null)
        {
            return string.Empty;
        }

        var summaries = this.todoResolver.PersistentStore.GetSummaries(session.ConversationId);
        if (summaries.Count == 0)
        {
            return string.Empty;
        }

        var section = "\n\n## Active plans\n";
        foreach (var summary in summaries)
        {
            section += TodoParser.FormatSummary(summary) + "\n";
        }

        return section;
    }

    // ── LoggerMessage source-generated methods ───────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt tail for {ConversationId}: [{LastRoles}], final={FinalRole}")]
    private partial void LogPromptTail(string conversationId, string lastRoles, string finalRole);

    [LoggerMessage(Level = LogLevel.Debug, Message = "System prompt fingerprint for {ConversationId}: {Fingerprint}")]
    private partial void LogPromptFingerprint(string conversationId, string fingerprint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[context] {ConversationId} before={BeforeMessages}msgs/{BeforeTokens}tok after={AfterMessages}msgs/{AfterTokens}tok window={ContextWindow} reserve={Reserve}")]
    private partial void LogBuildPrompt(string conversationId, int beforeMessages, int beforeTokens, int afterMessages, int afterTokens, int contextWindow, int reserve);

    [LoggerMessage(Level = LogLevel.Error, Message = "Context overflow for {ConversationId}: all {HistoryCount} conversation messages dropped by TrimToFit (estimatedTokens={EstimatedTokens}, contextWindow={ContextWindow}, reserveForResponse={ReserveForResponse})")]
    private partial void LogContextOverflow(string conversationId, int historyCount, int estimatedTokens, int contextWindow, int reserveForResponse);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Context trimmed for {ConversationId}: dropped {DroppedCount} of {HistoryCount} conversation messages (estimatedTokens={EstimatedTokens}, contextWindow={ContextWindow})")]
    private partial void LogContextTrimmed(string conversationId, int droppedCount, int historyCount, int estimatedTokens, int contextWindow);
}
