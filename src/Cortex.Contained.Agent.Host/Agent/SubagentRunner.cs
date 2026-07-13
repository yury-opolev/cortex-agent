using System.Collections.Frozen;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Thin wrapper around <see cref="AgentLoop"/> for subagent execution.
/// Provides the public API consumed by <see cref="Tools.BuiltIn.SubAgentStartTool"/>,
/// <see cref="Tools.BuiltIn.SubAgentSendTool"/>, and <see cref="SubagentRunnerRegistry"/>.
/// All loop logic is delegated to <see cref="AgentLoop"/> with <see cref="SubagentCallbacks"/>.
/// </summary>
public sealed class SubagentRunner : IDisposable
{
    /// <summary>Default safety-net round limit when none is configured.</summary>
    internal const int DefaultMaxRounds = 200;

    private readonly AgentLoop agentLoop;
    private readonly int maxRounds;
    private readonly IModelProvider? modelProvider;
    private readonly InMemoryTodoStore? todoStore;
    private readonly SubagentSessionStore? store;
    private readonly string? taskId;
    private readonly ILlmClient llmClient;
    private readonly ILogger logger;
    private readonly IOptionsMonitor<ImageAgingConfig>? imageAgingOptions;
    private readonly IImageDescriber? imageDescriber;

    /// <summary>
    /// Session used solely for its pending message queue.
    /// Allows <see cref="InjectMessage"/> to enqueue messages at any time
    /// (before or during execution), which are drained by
    /// <see cref="SubagentCallbacks.DrainInjectedMessages"/> each round.
    /// </summary>
    private readonly AgentSession pendingSession = new("subagent-pending");

    /// <summary>
    /// Tool names excluded from the subagent's tool definitions.
    /// Prevents recursion and controls scope.
    /// </summary>
    private static readonly FrozenSet<string> s_excludedTools = FrozenSet.ToFrozenSet(
        [
            "sub_agent_start", "sub_agent_read", "sub_agent_send", // no recursion
            "send_message",    // subagent must not message user directly
            "schedule_task",   // subagent should not create scheduled tasks
            "speak_after_delay", "cancel_delayed_speech", // voice-only; not for subagents
        ], StringComparer.OrdinalIgnoreCase);

    /// <summary>Default context window when model provider is not available.</summary>
    private const int FallbackContextWindow = 128_000;

    /// <summary>Simple constructor for tests and backward compatibility.</summary>
    public SubagentRunner(ILlmClient llmClient, Tools.ToolRegistry toolRegistry, int maxRounds, ILogger logger)
    {
        this.agentLoop = new AgentLoop(llmClient, toolRegistry,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentLoop>.Instance);
        this.maxRounds = maxRounds > 0 ? maxRounds : DefaultMaxRounds;
        this.llmClient = llmClient;
        this.logger = logger;
    }

    /// <summary>
    /// Persistent constructor for async subagent execution. Terminal state ownership belongs
    /// solely to <see cref="SubagentExecutionCoordinator"/> — the runner never records a terminal
    /// state itself (it returns a <see cref="SubagentExecutionResult"/> the coordinator persists once).
    /// </summary>
    public SubagentRunner(
        ILlmClient llmClient,
        Tools.ToolRegistry toolRegistry,
        int maxRounds,
        ILogger logger,
        SubagentSessionStore store,
        string taskId,
        IModelProvider modelProvider,
        InMemoryTodoStore? todoStore = null,
        IOptionsMonitor<ImageAgingConfig>? imageAgingOptions = null,
        IImageDescriber? imageDescriber = null)
        : this(llmClient, toolRegistry, maxRounds, logger)
    {
        this.store = store;
        this.taskId = taskId;
        this.modelProvider = modelProvider;
        this.todoStore = todoStore;
        this.imageAgingOptions = imageAgingOptions;
        this.imageDescriber = imageDescriber;
    }

    /// <summary>Full constructor with explicit AgentLoop (for DI and testing).</summary>
    public SubagentRunner(
        AgentLoop agentLoop,
        ILlmClient llmClient,
        int maxRounds,
        ILogger logger,
        SubagentSessionStore store,
        string taskId,
        IModelProvider modelProvider,
        InMemoryTodoStore? todoStore = null,
        IOptionsMonitor<ImageAgingConfig>? imageAgingOptions = null,
        IImageDescriber? imageDescriber = null)
    {
        this.agentLoop = agentLoop;
        this.maxRounds = maxRounds > 0 ? maxRounds : DefaultMaxRounds;
        this.llmClient = llmClient;
        this.logger = logger;
        this.store = store;
        this.taskId = taskId;
        this.modelProvider = modelProvider;
        this.todoStore = todoStore;
        this.imageAgingOptions = imageAgingOptions;
        this.imageDescriber = imageDescriber;
    }

    /// <summary>
    /// Inject a message into the running subagent. Thread-safe.
    /// Works before or during execution — messages are enqueued on
    /// the pending session and drained each round by the callbacks.
    /// </summary>
    public void InjectMessage(string message)
    {
        this.pendingSession.EnqueuePending(new AgentMessage
        {
            ConversationId = "subagent",
            ChannelId = "subagent",
            Text = message,
            Source = AgentMessageSource.User,
        });
    }

    /// <summary>
    /// Run the subagent from scratch with the given prompts. Returns the terminal outcome
    /// (state + result text) for the coordinator to persist exactly once.
    /// </summary>
    public async Task<SubagentExecutionResult> RunAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = userPrompt },
        };

        return await ExecuteAsync(model, messages, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resume from previously stored messages. Returns the terminal outcome
    /// (state + result text) for the coordinator to persist exactly once.
    /// </summary>
    public async Task<SubagentExecutionResult> ResumeAsync(
        string model,
        List<LlmMessage> existingMessages,
        string conversationId,
        CancellationToken cancellationToken)
    {
        return await ExecuteAsync(model, existingMessages, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps an <see cref="AgentLoopOutcome"/> to its terminal <see cref="SubagentTaskState"/> exactly
    /// once. Only <see cref="AgentLoopOutcome.Completed"/> is a success; everything else is a failure.
    /// </summary>
    private static SubagentTaskState ToTerminalState(AgentLoopOutcome outcome) => outcome switch
    {
        AgentLoopOutcome.Completed => SubagentTaskState.Completed,
        AgentLoopOutcome.Error => SubagentTaskState.Failed,
        AgentLoopOutcome.DoomLoop => SubagentTaskState.Failed,
        AgentLoopOutcome.MaxRoundsExceeded => SubagentTaskState.Failed,
        _ => SubagentTaskState.Failed,
    };

    private async Task<SubagentExecutionResult> ExecuteAsync(
        string model,
        List<LlmMessage> messages,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var contextWindow = this.modelProvider?.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var maxOutputTokens = TokenLimits.ResolveMaxOutput(this.modelProvider);

        var config = new AgentLoopConfig
        {
            MaxRounds = this.maxRounds,
            ExcludedTools = s_excludedTools,
            Model = model,
            MaxOutputTokens = maxOutputTokens,
            ContextWindow = contextWindow,
            ConversationId = conversationId,
            // Each subagent gets its OWN channel (its unique conversationId,
            // "subagent-{taskId}") rather than a shared constant, so coda sessions
            // started by concurrent subagents don't collide in one channel namespace
            // (which resolves to ambiguous_session). Coda keys sessions by channel.
            ChannelId = conversationId,
        };

        var callbacks = new SubagentCallbacks(
            messages,
            contextWindow,
            maxOutputTokens,
            conversationId,
            this.llmClient,
            this.logger,
            this.todoStore,
            this.store,
            this.taskId,
            this.pendingSession,
            this.imageAgingOptions?.CurrentValue,
            this.imageDescriber);

        var result = await this.agentLoop.ExecuteAsync(config, callbacks, cancellationToken).ConfigureAwait(false);

        // For non-completed outcomes, use the error message as the response text.
        // For completed with empty response (LLM put everything in tool calls),
        // include the last assistant message from the conversation as the result.
        var responseText = result.Outcome == AgentLoopOutcome.Completed
            ? result.ResponseText
            : result.ErrorMessage ?? result.ResponseText;

        if (string.IsNullOrWhiteSpace(responseText) && result.Outcome == AgentLoopOutcome.Completed)
        {
            // The LLM may have included results alongside a tool call (e.g., text + todos_write).
            // Find the last assistant message that has actual text content, even if it also has tool calls.
            var lastWithContent = messages.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content));
            responseText = lastWithContent?.Content ?? "[Subagent completed but produced no text response]";
        }

        // Persist the final assistant response so a later sub_agent_send can resume from it.
        // The agent loop returns the final text WITHOUT appending it to the message history
        // (the no-tool-call terminal path just returns), so we append it here for durability.
        if (this.store is not null && this.taskId is not null
            && result.Outcome == AgentLoopOutcome.Completed
            && !string.IsNullOrWhiteSpace(responseText))
        {
            var last = messages.Count > 0 ? messages[^1] : null;
            var alreadyPersisted = last is { Role: "assistant" }
                && string.Equals(last.Content, responseText, StringComparison.Ordinal);
            if (!alreadyPersisted)
            {
                messages.Add(new LlmMessage { Role = "assistant", Content = responseText });
                this.store.UpdateMessages(this.taskId, messages, result.RoundsExecuted);
            }
        }

        // Terminal state ownership belongs to the coordinator: the runner only reports the
        // outcome. It never writes a terminal state through the unguarded UpdateState path.
        return new SubagentExecutionResult(ToTerminalState(result.Outcome), responseText);
    }

    public void Dispose()
    {
        this.pendingSession.Dispose();
    }
}
