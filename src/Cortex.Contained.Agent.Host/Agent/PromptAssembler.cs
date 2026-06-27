using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
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
    private readonly ILogger<PromptAssembler> logger;

    /// <summary>
    /// Fallback context window when the model's actual limit is unknown.
    /// </summary>
    private const int FallbackContextWindow = 128_000;

    /// <summary>
    /// Additional system prompt injected when the user is communicating via a voice channel.
    /// Guides the LLM to produce responses suitable for spoken output.
    /// </summary>
    internal const string VoiceModeInstructions = """


        ## Voice mode

        You are currently speaking to the user via VOICE. Your responses will be
        read aloud through text-to-speech. Adjust your communication:

        1. CONVERSATIONAL TONE: Use contractions, active voice, plain language.
           Speak as you would to a friend. Avoid unnecessary jargon.

        2. NO FORMATTING: Never use markdown, bullet points, numbered lists, code
           blocks, URLs, file paths, or email addresses. Everything you say will
           be spoken aloud — write in natural flowing speech.

        3. PROGRESSIVE DISCLOSURE: When a topic has a lot of detail, give the key
           point first, then offer to go deeper. Don't front-load with caveats.

        4. NUMBERS: Say "about twelve hundred" not "1,247". Round when possible.
           Spell out abbreviations.

        5. COMPLEX CONTENT: You cannot speak code, URLs, tables, or long technical
           output. Instead, ask the user if you should send it to the chat (use
           the send_message tool targeting discord-dm). Say something like "I have
           some code for that. Want me to send it to the chat?"

        6. NATURAL LENGTH: Respond naturally — short answers for simple questions,
           longer responses when the user asks for stories, explanations, or detail.
           Don't artificially truncate, but don't pad either.
        """;

    /// <summary>
    /// System-prompt addendum for the coding agent (Coda) relay.
    /// Teaches the LLM when to invoke the <c>coding_*</c> tools and how to
    /// interpret the <c>[coding ...]</c> injection envelopes that arrive
    /// when Coda emits terminal events.
    /// </summary>
    private const string CodingAgentRelayPrompt = """


        ## Coding agent (Coda) relay
        You can drive a separate coding agent (Coda, running on the host) using eleven
        `coding_*` tools. Use this for coding tasks on real projects in allowed folders on
        the user's host — NOT for changes to your own behavior or this codebase.

        Tools:
          - `coding_session_start({workingFolder, policy?, sessionName?, goal?, sessionMemory?})` — start a session.
            `workingFolder` is mandatory and must be an absolute path to an allowlisted folder OR a child of one.
            Use `coding_folders_list` first if you are unsure whether the folder is allowed.
            `policy` (optional string: "Prompt" | "YoloSafe" | "Yolo"): permission mode. Omit to use the folder's
            configured default policy. You may only request a MORE restrictive policy than the folder allows
            (e.g. "Prompt" when the folder ceiling is "YoloSafe"). Never request a more permissive policy than the ceiling.
            `goal` (string, off by default): autonomous objective — Coda keeps running until met; use only when the user explicitly asks.
            `sessionMemory` (bool, off by default): enable Coda's session-memory feature; use only when the user explicitly asks.
          - `coding_session_send({sessionId?, message})` — send an instruction; returns
            immediately. The result arrives later as an injected envelope.
          - `coding_session_respond({requestId, response})` — reply to a pending request
            (see Envelopes below). `requestId` comes from the envelope header.
            Response meaning depends on request kind:
              - permission → `"allow_once"` | `"allow_always"` | `"deny"`
              - question  → the chosen option or a free-form answer
              - plan      → `"approve"` | `"reject"`
          - `coding_session_status({sessionId?})` — query a session's latest state. Includes the
            session state, recent tool calls, token usage, last error (if any), the path to
            Coda's telemetry log for the run, AND live progress (`isStreaming`, `currentActivity`,
            `streamedChars`, `lastStreamActivityAt`). Use the live fields to tell whether Coda is
            actively working (streaming a response / running a long step) vs idle BEFORE assuming
            it is stuck — a recent `lastStreamActivityAt` or `isStreaming=true` means it is alive.
            For an autonomous run it also includes `goalStatus` (outcome, continuations used,
            elapsed seconds, and what still remains) — use it to report goal progress.
          - `coding_session_set_goal({sessionId?, goal?, maxDuration?, maxContinuations?})` — set,
            replace, or CLEAR a session's autonomous goal (Coda then works on its own until a judge
            says the goal is met or the budget runs out). Pass an empty `goal` (or omit it) to CLEAR
            the goal and return to interactive mode. Coda does not merge — include the full goal text
            when changing the budget. Takes effect from the next `coding_session_send`. Only use this
            when the user explicitly asks to start (or stop) autonomous/goal-driven execution.
          - `coding_session_history({sessionId?, sinceIndex?})` — read the session's actual
            transcript (role/content messages). Omit `sinceIndex` for the full conversation; pass
            the `nextIndex` from a prior call to fetch only new messages since then. Use this to
            answer "what did Coda actually do/say?".
          - `coding_session_list()` — list all sessions for this channel.
          - `coding_session_interrupt({sessionId?})` — interrupt a running session.
          - `coding_session_end({sessionId?})` — end a session.
          - `coding_session_resume({sessionId, workingFolder, policy?})` — re-attach to a
            past session by ID. Same allowlist and policy rules as `coding_session_start`.
          - `coding_folders_list()` — list allowed working folders and their configured policies.
            Use this to answer "what projects can you work on?" and to resolve a loosely-named
            project before starting a session. A session can only run inside a listed folder or
            a child of one.

        Multiple sessions:
          You may run several coding sessions at once (e.g. different projects), up to a
          per-tenant limit. When a channel has more than one active session, ALWAYS pass the
          explicit `sessionId` to `coding_session_send`, `coding_session_status`,
          `coding_session_interrupt`, and `coding_session_end` — omitting it returns an
          `ambiguous_session` error listing the active sessions. When you relay an envelope to
          the user, say which session it is from (use `coding_session_list` / `coding_session_status`
          to map a `session=<id>` to its name and working folder). If starting a session fails
          with `max_sessions_reached`, tell the user to end one first.

        Envelopes:
          When a session emits an event, you receive a synthetic user-role message starting
          with `[coding session=… status=… …]`. Treat it as a relay packet, not a request
          for help. Possible statuses:
            - `status=ready`: Coda finished a task. Below the header is `Final:` prose and
              optionally `Tools:` (list of what it did).
            - `status=awaiting-permission`: Coda wants permission for a tool. The envelope
              shows the tool name and input preview. Ask the user to allow_once / allow_always /
              deny, then call `coding_session_respond` with the `requestId` and their answer.
            - `status=awaiting-question`: Coda has a question. The envelope shows `Question:`
              and optionally `Options:` (pipe-separated choices). Relay the question and options
              to the user, then call `coding_session_respond` with the `requestId` and their
              chosen option or free-form answer.
            - `status=awaiting-plan`: Coda is requesting plan approval. The envelope shows the
              plan text. Relay a short summary to the user; call `coding_session_respond` with
              `requestId` and `"approve"` or `"reject"`.
            - `status=crashed`: the session died. Surface the error and offer to resume or end.
            - `status=stalled`: Coda went unresponsive mid-turn and the watchdog terminated it
              (a stall, not a logic error — often a hung model call). The header shows `idleSeconds`.
              Tell the user it stalled and offer to resume it (`coding_session_resume`) or end it.
              Do NOT immediately resend the same instruction or start a duplicate session.

        Relay rules — voice channels:
          - Relay only prose (`Final:` text, questions, plan summaries). Do NOT narrate `Tools:`
            or per-call details unless the user explicitly asks.
          - For long plans, diffs, or code blocks: post the full content to a paired text channel
            via `send_message` and reference it in voice ("I sent the plan to #text").
          - Preserve verbatim: file paths, command names, identifier names, line numbers.
            They are how the user steers — do not paraphrase.

        Relay rules — text channels:
          - Relay prose only by default; surface tool calls on ask.
          - Preserve markdown (code fences, diffs). Split messages over ~2000 chars with `(1/N)`.

        Strict pass-through:
          - Do not editorialize or add opinions about Coda's output. You are a relay.

        Anti-churn (avoid spamming Coda):
          - A coding task can legitimately run for many minutes (long model calls, big steps).
            While a session is `Working` or `isStreaming=true`, do NOT resend the instruction,
            start a duplicate session, or interrupt it just because it has not replied yet —
            call `coding_session_status` first and check `lastStreamActivityAt`/`currentActivity`.
          - Only treat a session as stuck when you receive a `status=stalled` envelope or
            `coding_session_status` shows no recent activity. Then resume or end it — once.

        """;

    public PromptAssembler(
        Func<string> loadPersonality,
        IModelProvider modelProvider,
        IOptionsMonitor<ImageAgingConfig> imageAgingOptions,
        ILogger<PromptAssembler> logger,
        SelfNotesStore? selfNotesStore = null,
        SkillRegistry? skillRegistry = null,
        SubagentSessionStore? subagentStore = null,
        TodoStoreResolver? todoResolver = null,
        IImageDescriber? imageDescriber = null)
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

        var systemPrompt = personality + "\n\n## Self-notes\n" + selfNotes + skillsSection;

        // Channel context (tiny, tells agent where the conversation is)
        var channelLabel = GetChannelLabel(channelId);
        if (channelLabel is not null)
        {
            systemPrompt += $"\nThe user is currently talking to you via {channelLabel}.";
        }

        // Voice mode instructions (conditional, for spoken output)
        if (isVoice)
        {
            systemPrompt += VoiceModeInstructions;
        }

        // Voice-enrollment is opt-in only — entry points are an explicit user
        // request (the LLM sees start_voice_enrollment in its tool list),
        // the /voice-id enroll slash command, or the Web UI "Start
        // enrollment" button. No proactive prompts from the agent.

        // Active background tasks (operational state)
        if (this.subagentStore is not null)
        {
            var activeTasks = this.subagentStore.GetActive();
            if (activeTasks.Count > 0)
            {
                systemPrompt += "\n\n## Active background tasks\n";
                foreach (var task in activeTasks)
                {
                    var elapsed = (DateTimeOffset.UtcNow - task.CreatedAt).TotalMinutes;
                    var stateLabel = task.State.ToStorageValue();
                    systemPrompt += $"- [{task.TaskId}] \"{task.Description}\" ({stateLabel}, {elapsed:F0}m ago)\n";
                }
            }
        }

        // Active plans (operational state)
        if (this.todoResolver is not null)
        {
            var summaries = this.todoResolver.PersistentStore.GetSummaries(session.ConversationId);
            if (summaries.Count > 0)
            {
                systemPrompt += "\n\n## Active plans\n";
                foreach (var summary in summaries)
                {
                    systemPrompt += TodoParser.FormatSummary(summary) + "\n";
                }
            }
        }

        // Coding agent (Coda) relay — relay rules + envelope semantics.
        systemPrompt += CodingAgentRelayPrompt;

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

    // ── LoggerMessage source-generated methods ───────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Prompt tail for {ConversationId}: [{LastRoles}], final={FinalRole}")]
    private partial void LogPromptTail(string conversationId, string lastRoles, string finalRole);

    [LoggerMessage(Level = LogLevel.Debug, Message = "[context] {ConversationId} before={BeforeMessages}msgs/{BeforeTokens}tok after={AfterMessages}msgs/{AfterTokens}tok window={ContextWindow} reserve={Reserve}")]
    private partial void LogBuildPrompt(string conversationId, int beforeMessages, int beforeTokens, int afterMessages, int afterTokens, int contextWindow, int reserve);

    [LoggerMessage(Level = LogLevel.Error, Message = "Context overflow for {ConversationId}: all {HistoryCount} conversation messages dropped by TrimToFit (estimatedTokens={EstimatedTokens}, contextWindow={ContextWindow}, reserveForResponse={ReserveForResponse})")]
    private partial void LogContextOverflow(string conversationId, int historyCount, int estimatedTokens, int contextWindow, int reserveForResponse);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Context trimmed for {ConversationId}: dropped {DroppedCount} of {HistoryCount} conversation messages (estimatedTokens={EstimatedTokens}, contextWindow={ContextWindow})")]
    private partial void LogContextTrimmed(string conversationId, int droppedCount, int historyCount, int estimatedTokens, int contextWindow);
}
