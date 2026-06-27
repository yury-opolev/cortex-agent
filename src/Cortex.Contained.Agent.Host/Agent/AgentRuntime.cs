using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Channels;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Pipeline;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Options;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Core agent runtime. Enqueues inbound messages into a
/// <see cref="AgentMessageChannel"/> and routes them to per-session
/// processing loops. Each session runs its own event loop that drains
/// the session's pending queue and processes messages one at a time.
/// Different sessions run in parallel. Each session loop orchestrates
/// LLM calls, multi-turn tool execution, and streams responses back
/// through the Bridge using <see cref="BridgeClientAccessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Several constructor parameters are nullable <em>by design</em>. In production
/// (<c>Program.cs</c>) every one of them is always supplied via DI; the nullability exists
/// only so tests can construct an <see cref="AgentRuntime"/> cheaply without standing up the
/// full graph (memory pipeline, SQLite stores, image describer, etc.). Those collaborators
/// (<c>memoryExtraction</c>, <c>memoryService</c>, <c>embeddingService</c>, <c>subagentStore</c>,
/// <c>todoResolver</c>, <c>selfNotesStore</c>, <c>skillRegistry</c>, <c>imageDescriber</c>,
/// <c>compactionOptions</c>) are guarded with feature-presence checks at their (few) use sites.
/// </para>
/// <para>
/// The one high-traffic exception is the message store: its <c>is not null</c> checks were the
/// most numerous, so it is normalised to the non-nullable <see cref="Storage.IMessageStore"/>
/// abstraction. A null constructor argument is coalesced to the no-op
/// <see cref="Storage.NullMessageStore"/>, letting persistence call sites run unconditionally.
/// </para>
/// </remarks>
public sealed partial class AgentRuntime : IAgentRuntime, IBootstrapContextStore
{
    private readonly AgentSessionStore sessions;
    private readonly ILlmClient llmClient;
    private readonly ToolRegistry toolRegistry;
    private readonly SessionConfig sessionConfig;
    private readonly AgentMessageChannel messageQueue;
    private readonly BridgeClientAccessor bridgeClientAccessor;
    private readonly ILogger<AgentRuntime> logger;
    private readonly string personalityPath;

    // personality.md is read on every prompt build (every tool-loop round). Cache the parsed
    // content keyed on the file's last-write time so a turn re-reads from disk only when the
    // file actually changed (live edits + WritePersonality still take effect immediately).
    private readonly object personalityCacheLock = new();
    private string? cachedPersonality;
    private DateTime cachedPersonalityWriteUtc;

    private readonly string bootstrapContextPath;
    private readonly ActiveChannelStore activeChannelStore;
    private readonly IModelProvider modelProvider;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly Memory.MemoryExtractionService? memoryExtraction;
    private readonly Memory.MemorySettingsStore? memorySettingsStore;
    private readonly MemoryMcp.Core.Services.IMemoryService? memoryService;
    private readonly Storage.IMessageStore messageStore;
    private readonly MemoryMcp.Core.Services.IEmbeddingService? embeddingService;
    private readonly SubagentSessionStore? subagentStore;
    private readonly TodoStoreResolver? todoResolver;
    private readonly SelfNotesStore? selfNotesStore;
    private readonly SkillRegistry? skillRegistry;
    private readonly IOptionsMonitor<ImageAgingConfig> imageAgingOptions;
    private readonly IImageDescriber? imageDescriber;
    private readonly IOptionsMonitor<ConversationCompactionConfig>? compactionOptions;
    private readonly AgentMetrics? metrics;

    // ── Extracted collaborators (constructed in the ctor from existing deps) ──
    private readonly PromptAssembler promptAssembler;
    private readonly CompactionOrchestrator compaction;
    private readonly SlashCommandHandler slashCommandHandler;

    private CancellationTokenSource? consumerCts;
    private Task? consumerTask;

    /// <summary>
    /// Per-session processing loops. Each session runs exactly one loop at a time,
    /// draining pending messages and processing them sequentially.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task> sessionLoops = new();

    private string defaultModel => modelProvider.DefaultModel;
    private int maxTokens = 8192;
    private double temperature = 0.7;

    /// <summary>
    /// Safety-net round limit. The real termination signals are: model stops calling tools,
    /// context window fills up, or doom loop detection fires. This cap exists only as a
    /// last-resort circuit breaker for truly pathological cases.
    /// </summary>
    private const int MaxToolRounds = 200;

    private static readonly char[] InlineCommandWordSeparators = [' ', '\t'];



    /// <summary>
    /// Default personality used when personality.md does not exist.
    /// </summary>
    private const string DefaultPersonality = PersonalityDefaults.DefaultPersonality;

    /// <summary>
    /// Default content for context-bootstrap.md when it does not exist yet.
    /// Guides the agent to actively learn about the user on first conversation.
    /// </summary>
    private const string DefaultBootstrapContext = """
        You do not have any context about the user yet. This is either a brand new conversation
        or the first time you are talking to this person. Introduce yourself and ask about them —
        their name, what they do, their interests, where they are based, and how you can help.
        Once you learn their basic identity, use the context_bootstrap_update tool to save it
        so you remember them next time.
        """;

    /// <summary>
    /// Base system instructions appended after the personality.
    /// Tells the agent about the personality file so it can self-modify.
    /// </summary>
    // SystemInstructions removed — all operational guidance lives in self-notes (agent-managed).
    // The agent discovers tools from tool descriptions. Personality file editing is explained
    // by the personality tool's own description.

    /// <summary>
    /// Minimum number of tool-loop rounds between compaction attempts.
    /// Prevents compacting on every round once past the threshold.
    /// </summary>
    private const int MinRoundsBetweenCompactions = 3;

    public AgentRuntime(
        AgentSessionStore sessions,
        ILlmClient llmClient,
        ToolRegistry toolRegistry,
        SessionConfig sessionConfig,
        AgentMessageChannel messageQueue,
        BridgeClientAccessor bridgeClientAccessor,
        ActiveChannelStore activeChannelStore,
        IHttpClientFactory httpClientFactory,
        string sandboxRoot,
        string stateRoot,
        ILogger<AgentRuntime> logger,
        IModelProvider modelProvider,
        IOptionsMonitor<ImageAgingConfig> imageAgingOptions,
        Memory.MemoryExtractionService? memoryExtraction = null,
        MemoryMcp.Core.Services.IMemoryService? memoryService = null,
        MemoryMcp.Core.Services.IEmbeddingService? embeddingService = null,
        Storage.IMessageStore? messageStore = null,
        SubagentSessionStore? subagentStore = null,
        TodoStoreResolver? todoResolver = null,
        SelfNotesStore? selfNotesStore = null,
        SkillRegistry? skillRegistry = null,
        IImageDescriber? imageDescriber = null,
        IOptionsMonitor<ConversationCompactionConfig>? compactionOptions = null,
        AgentMetrics? metrics = null,
        ILoggerFactory? loggerFactory = null,
        Memory.MemorySettingsStore? memorySettingsStore = null)
    {
        this.subagentStore = subagentStore;
        this.todoResolver = todoResolver;
        this.selfNotesStore = selfNotesStore;
        this.skillRegistry = skillRegistry;
        this.sessions = sessions;
        this.llmClient = llmClient;
        this.toolRegistry = toolRegistry;
        this.sessionConfig = sessionConfig;
        this.messageQueue = messageQueue;
        this.bridgeClientAccessor = bridgeClientAccessor;
        this.activeChannelStore = activeChannelStore;
        this.httpClientFactory = httpClientFactory;
        this.personalityPath = Path.Combine(sandboxRoot, "personality.md");
        this.bootstrapContextPath = Path.Combine(stateRoot, "context-bootstrap.md");
        this.logger = logger;
        this.modelProvider = modelProvider;
        this.memoryExtraction = memoryExtraction;
        this.memorySettingsStore = memorySettingsStore;
        this.memoryService = memoryService;
        this.embeddingService = embeddingService;
        this.messageStore = messageStore ?? Storage.NullMessageStore.Instance;
        this.imageAgingOptions = imageAgingOptions;
        this.imageDescriber = imageDescriber;
        this.compactionOptions = compactionOptions;
        this.metrics = metrics;

        // Construct the extracted collaborators from already-injected dependencies.
        // Done here (rather than via DI) so the many test construction sites that use
        // the minimal ctor keep working unchanged. Collaborators get their own typed
        // loggers from the factory when available; tests pass none and fall back to
        // NullLogger (the moved log messages then no-op, exactly as a missing factory
        // would). SourceContext for the moved messages changes from "AgentRuntime" to
        // "PromptAssembler"/"CompactionOrchestrator".
        this.compaction = new CompactionOrchestrator(
            llmClient,
            modelProvider,
            imageAgingOptions,
            loggerFactory?.CreateLogger<CompactionOrchestrator>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CompactionOrchestrator>.Instance,
            memoryExtraction,
            compactionOptions,
            imageDescriber,
            memorySettingsStore);
        this.promptAssembler = new PromptAssembler(
            this.LoadPersonality,
            modelProvider,
            imageAgingOptions,
            loggerFactory?.CreateLogger<PromptAssembler>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PromptAssembler>.Instance,
            selfNotesStore,
            skillRegistry,
            subagentStore,
            todoResolver,
            imageDescriber);
        this.slashCommandHandler = new SlashCommandHandler(
            bridgeClientAccessor,
            this.messageStore,
            modelProvider,
            this.compaction);

        // Expose the live count of sessions currently generating a response so the
        // health snapshot reports active conversations without the metrics object
        // needing a reference to the session store.
        this.metrics?.SetActiveConversationsProvider(
            () => this.sessions.GetAll().Count(s => s.IsGenerating));
    }

    // ── Queue entry point ────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<SendMessageResult> HandleMessageAsync(
        HubInboundMessage message,
        CancellationToken cancellationToken)
    {
        var correlationId = message.CorrelationId ?? Guid.NewGuid().ToString("N");

        var agentMessage = new AgentMessage
        {
            ConversationId = message.ConversationId,
            ChannelId = message.ChannelId,
            Text = message.Text,
            Source = AgentMessageSource.User,
            SenderIdHash = message.SenderIdHash,
            Attachments = message.Attachments,
            CorrelationId = correlationId,
            Timestamp = message.Timestamp,
            IsVoice = message.IsVoice,
        };

        await this.messageQueue.EnqueueAsync(agentMessage, cancellationToken).ConfigureAwait(false);

        this.LogMessageEnqueued(message.ConversationId, correlationId);

        return new SendMessageResult
        {
            Accepted = true,
            ConversationId = message.ConversationId,
        };
    }

    // ── Consumer loop ────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        this.consumerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.consumerTask = Task.Run(() => ConsumerLoopAsync(this.consumerCts.Token), CancellationToken.None);
        this.LogConsumerStarted();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopProcessingAsync(CancellationToken cancellationToken)
    {
        // Cancel the consumer first so it exits the ReadAllAsync loop via
        // OperationCanceledException, then mark the channel complete to
        // prevent further writes. This avoids ChannelClosedException when
        // the consumer is still iterating as the channel is completed.
        if (this.consumerCts is not null)
        {
            await this.consumerCts.CancelAsync().ConfigureAwait(false);
        }

        this.messageQueue.Complete();

        if (this.consumerTask is not null)
        {
            try
            {
                await this.consumerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        // Wait for all session loops to complete
        var loops = this.sessionLoops.Values.ToArray();
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown deadline exceeded
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                this.LogConsumerMessageError("shutdown", "drain", ex.Message);
            }
        }

        this.consumerCts?.Dispose();
        this.consumerCts = null;
        this.consumerTask = null;
        this.LogConsumerStopped();
    }

    /// <summary>
    /// Routes incoming messages to the correct session's pending queue.
    /// Each session runs its own processing loop.
    /// </summary>
    private async Task ConsumerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in this.messageQueue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                this.LogMessageDispatched(message.ConversationId, message.CorrelationId, message.ConversationId);

                var session = this.sessions.GetOrCreate(message.ConversationId);
                session.EnqueuePending(message);

                // Ensure a processing loop is running for this session
                this.EnsureSessionLoop(session, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (ChannelClosedException)
        {
            // Channel completed during shutdown
        }
    }

    /// <summary>
    /// Ensures a processing loop is running for the given session.
    /// If one is already running, this is a no-op.
    /// </summary>
    private void EnsureSessionLoop(AgentSession session, CancellationToken cancellationToken)
    {
        this.sessionLoops.GetOrAdd(session.ConversationId, _ =>
        {
            Task? taskRef = null;
            taskRef = Task.Run(async () =>
            {
                try
                {
                    await this.SessionLoopAsync(session, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutting down
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    this.LogConsumerMessageError(session.ConversationId, "session-loop", ex.Message);
                }
                finally
                {
                    // Use KeyValuePair overload to only remove our own entry,
                    // not a replacement that may have been added concurrently.
                    if (taskRef is not null)
                    {
                        this.sessionLoops.TryRemove(new KeyValuePair<string, Task>(session.ConversationId, taskRef));
                    }
                }
            }, CancellationToken.None);

            return taskRef;
        });
    }

    /// <summary>
    /// Per-conversation event loop. Waits for messages, drains them,
    /// and processes through the LLM tool loop. Runs until cancelled.
    /// </summary>
    private async Task SessionLoopAsync(AgentSession session, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Wait for at least one message
            await session.WaitForPendingAsync(cancellationToken).ConfigureAwait(false);

            // Drain all pending messages
            var messages = session.DrainPendingMessages();
            if (messages.Count == 0)
            {
                continue;
            }

            // Process the first message through the existing flow.
            // Additional messages (if any) are available for mid-turn drain.
            var firstMessage = messages[0];

            // Add remaining messages back to the queue so they can be
            // drained between tool rounds by GenerateResponseAsync.
            for (var i = 1; i < messages.Count; i++)
            {
                session.EnqueuePending(messages[i]);
            }

            try
            {
                await this.ProcessQueuedMessageAsync(firstMessage, cancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                this.LogConsumerMessageError(session.ConversationId, firstMessage.CorrelationId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Process a single message from the queue. Resolves the session,
    /// adds the message to history, runs the LLM generation loop, and
    /// streams the response back through the Bridge.
    /// </summary>
    private async Task ProcessQueuedMessageAsync(AgentMessage message, CancellationToken cancellationToken)
    {
        this.metrics?.IncrementMessagesProcessed();

        // Resolve the per-source processing policy once; all source-conditional branches below use it.
        var behavior = MessageSourceBehavior.For(message.Source);

        // Scheduled tasks run in isolated ephemeral sessions (D12):
        // fresh empty session, no history, discard after execution.
        // Subagent completions are NOT ephemeral — they run on the parent conversation.
        var isEphemeral = behavior.RunInEphemeralSession;
        AgentSession session;

        if (isEphemeral)
        {
            session = new AgentSession(message.ConversationId);
        }
        else
        {
            // Use the channel ID directly as the conversation/session key (no more SessionScope resolution)
            session = this.sessions.GetOrCreateWithIdleCheck(message.ConversationId);

            // If the session was flagged for idle compaction (instead of wiped),
            // run LLM summarization now to preserve important context before
            // the new message is added to history.
            if (session.NeedsIdleCompaction)
            {
                session.NeedsIdleCompaction = false;
                if (this.memoryExtraction is not null)
                {
                    this.compaction.FlushExtractionBuffer(session, message.ConversationId);
                }

                await this.compaction.CompactConversationAsync(session, cancellationToken).ConfigureAwait(false);
                this.LogIdleCompactionPerformed(session.ConversationId);
            }
        }

        // Intercept slash commands before they enter the LLM pipeline.
        // These are handled locally and do not consume tokens.
        if (behavior.HandlesSlashCommands)
        {
            var trimmedText = message.Text.TrimStart();
            if (trimmedText.StartsWith("/context", StringComparison.OrdinalIgnoreCase)
                || trimmedText.StartsWith("/compact", StringComparison.OrdinalIgnoreCase))
            {
                await this.slashCommandHandler.HandleSlashCommandAsync(trimmedText, session, message, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        // Auto-set title from first message (user messages only, non-ephemeral)
        if (behavior.SetsConversationTitleFromText && session.Title is null && message.Text.Length > 0)
        {
            session.Title = message.Text.Length > 50
                ? string.Concat(message.Text.AsSpan(0, 47), "...")
                : message.Text;
        }

        // For scheduled tasks, the enriched text from SchedulerService.BuildEnrichedMessageText
        // already includes full task context (ID, description, last run, etc.).
        var messageText = message.Text;

        // Sanitize the input
        var sanitizedText = ContentSanitizer.Sanitize(messageText);

        // Reject empty messages (e.g. voice attachments without transcription)
        if (string.IsNullOrWhiteSpace(sanitizedText) && message.Attachments is null or { Count: 0 })
        {
            this.LogEmptyMessageRejected(session.ConversationId, message.ChannelId);
            return;
        }

        // Add to session history. Scheduled task instructions and subagent completions
        // are marked as internal — they appear in LLM context but not in the user's chat history.
        var isInternal = behavior.IsInternalToHistory;

        // Build content blocks if the message has image attachments
        var contentBlocks = await BuildContentBlocksAsync(
            sanitizedText, message.Attachments, cancellationToken).ConfigureAwait(false);

        session.AddMessage(new LlmMessage
        {
            Role = "user",
            Content = sanitizedText,
            ContentBlocks = contentBlocks,
            MessageType = isInternal ? LlmMessageType.ScheduledTaskInstruction : LlmMessageType.Normal,
        });

        // Persist inbound message to local MessageStore
        var inboundCategory = isInternal
            ? Contracts.Hub.MessageCategory.Internal
            : Contracts.Hub.MessageCategory.Normal;
        await this.messageStore.SaveMessageAsync(
            userId: message.SenderIdHash ?? "unknown",
            channelId: message.ChannelId,
            role: "user",
            content: AppendAttachmentPlaceholders(sanitizedText, message.Attachments),
            timestamp: message.Timestamp,
            category: inboundCategory,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Reset compaction flag so it can trigger again in this new turn
        session.LastCompactionRound = -1;

        // Trim if history is too long
        session.TrimHistory(this.sessionConfig.MaxHistory);

        var generationToken = session.BeginGeneration(cancellationToken);

        try
        {
            // Scheduled tasks deliver via OnScheduledTaskComplete (history-only).
            // Everything else (user messages, subagent completions) streams normally.
            var useProactiveDelivery = behavior.UseProactiveDelivery;

            // Use the original conversation ID (not the session-resolved one) for callbacks
            // so the Bridge routes responses to the correct channel.
            await GenerateResponseAsync(session, message.ConversationId, message.ChannelId, message.CorrelationId, useProactiveDelivery, messageText, message.IsVoice, generationToken)
                .ConfigureAwait(false);

            // Memory extraction: append to the extraction buffer. The buffer
            // is only flushed to the extraction service on compaction — while
            // the conversation is active, all information is already in context.
            if (this.memoryExtraction is not null && behavior.RunsMemoryExtraction)
            {
                var lastAssistant = session.GetHistory().LastOrDefault(m => m.Role == "assistant");
                if (lastAssistant?.Content is not null)
                {
                    session.AppendToExtractionBuffer(new ExtractionEntry
                    {
                        Role = "user",
                        Content = sanitizedText,
                        Timestamp = message.Timestamp,
                    });
                    session.AppendToExtractionBuffer(new ExtractionEntry
                    {
                        Role = "assistant",
                        Content = lastAssistant.Content,
                        Timestamp = DateTimeOffset.UtcNow,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            this.LogGenerationCancelled(session.ConversationId);
            throw;
        }
        catch (Exception ex)
        {
            this.LogGenerationError(session.ConversationId, ex.Message);

            try
            {
                var client = this.bridgeClientAccessor.Client;
                if (client is not null)
                {
                    await client.OnError(new AgentErrorMessage
                    {
                        ConversationId = message.ConversationId,
                        ErrorCode = ErrorCodes.InternalError,
                        Message = ex.Message,
                        IsRetryable = true,
                        CorrelationId = message.CorrelationId,
                    }).ConfigureAwait(false);

                    // Persist error response to history
                    await this.messageStore.SaveMessageAsync(
                        userId: "assistant",
                        channelId: message.ChannelId,
                        role: "assistant",
                        content: $"Error: {ex.Message}",
                        timestamp: DateTimeOffset.UtcNow,
                        category: Contracts.Hub.MessageCategory.System,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception callbackEx)
            {
                this.LogGenerationError(session.ConversationId, $"Failed to report error to Bridge: {callbackEx.Message}");
            }
        }
        finally
        {
            session.EndGeneration();

        }
    }

    private async Task GenerateResponseAsync(
        AgentSession session,
        string replyConversationId,
        string channelId,
        string correlationId,
        bool useProactiveDelivery,
        string? instructionText,
        bool isVoice,
        CancellationToken cancellationToken)
    {
        var client = this.bridgeClientAccessor.Client;
        if (client is null)
        {
            this.LogBridgeNotConnected(session.ConversationId);
            throw new InvalidOperationException("Bridge is not connected. Cannot generate response.");
        }

        // Notify status: streaming
        await client.OnStatusChanged(new AgentStatusInfo
        {
            Status = AgentStatus.Streaming,
            ActiveConversations = this.sessions.GetAll().Count(s => s.IsGenerating),
            CurrentModel = this.defaultModel,
            Uptime = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);

        var toolDefinitions = this.toolRegistry.GetDefinitionsForConversation(session.ConversationId);

        // No LLM calls for memory retrieval — agent uses memory_search when needed.
        // Self-notes (agent's operational knowledge) are read directly from the store.

        var doomLoopDetector = new DoomLoopDetector();

        var delivery = new TurnResponseDelivery(
            client, this.messageStore, replyConversationId, channelId, correlationId, useProactiveDelivery, this.logger);

        // Shared context for the entire turn — collects proactive messages
        // sent via send_message tool for deferred injection after the turn.
        var turnContext = new ToolExecutionContext
        {
            ConversationId = session.ConversationId,
            ChannelId = channelId,
            CorrelationId = correlationId,
        };

        for (var round = 0; round < MaxToolRounds; round++)
        {
            // Build the LLM request with tool definitions
            var messages = await this.promptAssembler.BuildPromptAsync(session, cancellationToken, channelId, isVoice).ConfigureAwait(false);
            var requestId = Guid.NewGuid().ToString("N");

            var request = new LlmCompletionRequest
            {
                Model = this.defaultModel,
                Messages = messages,
                Temperature = this.temperature,
                MaxTokens = TokenLimits.ResolveMaxOutput(this.modelProvider),
                RequestId = requestId,
                ConversationId = session.ConversationId,
                Tools = toolDefinitions.Count > 0 ? toolDefinitions : null,
            };

            var streamed = await this.StreamLlmTurnAsync(request, delivery, session, round, replyConversationId, cancellationToken).ConfigureAwait(false);
            if (streamed.Outcome == StreamOutcome.RetryAfterCompaction)
            {
                continue;
            }

            if (streamed.Outcome == StreamOutcome.Errored)
            {
                return;
            }

            // Track actual prompt token usage from the API response
            if (streamed.Usage is not null)
            {
                session.LastPromptTokens = streamed.Usage.PromptTokens + streamed.Usage.CacheWriteTokens + streamed.Usage.CacheReadTokens;
                this.LogTokenUsage(session.ConversationId, round + 1, streamed.Usage.PromptTokens, streamed.Usage.CompletionTokens, streamed.Usage.TotalTokens, session.MessageCount, streamed.Usage.CacheWriteTokens, streamed.Usage.CacheReadTokens);
            }

            if (streamed.ToolCalls.Count > 0)
            {
                var assistantContent = streamed.Text.Length > 0 ? streamed.Text : null;
                if (await this.ExecuteToolRoundAsync(session, delivery, turnContext, streamed.ToolCalls, assistantContent, doomLoopDetector, round, replyConversationId, cancellationToken).ConfigureAwait(false) == ToolRoundOutcome.DoomHalted)
                {
                    return;
                }

                continue;
            }

            // No tool calls -- this is a final text response.
            // Don't store empty assistant messages — they corrupt the history and cause
            // Anthropic to reject requests ("text content blocks must be non-empty").
            var responseText = streamed.Text;
            if (responseText.Length > 0)
            {
                session.AddMessage(new LlmMessage { Role = "assistant", Content = responseText });
            }

            var messageId = Guid.NewGuid().ToString("N");
            await delivery.DeliverFinalResponseAsync(
                session, responseText, instructionText, streamed.Usage, streamed.SequenceNumber, messageId, cancellationToken).ConfigureAwait(false);

            // Done -- exit the tool loop
            break;
        }

        // Inject proactive messages sent during this turn into target sessions.
        // Deferred until after the tool loop to avoid breaking tool_call → tool ordering.
        if (turnContext.ProactiveMessages.Collected.Count > 0)
        {
            foreach (var proactive in turnContext.ProactiveMessages.Collected)
            {
                var targetSession = this.sessions.GetOrCreateWithIdleCheck(proactive.ChannelId);
                targetSession.AppendOrGlueAssistantMessage(FormatProactiveHistoryEntry(proactive));
                this.LogProactiveMessageInjected(proactive.ChannelId, proactive.Text.Length);
            }
        }

        // Notify idle
        await client.OnStatusChanged(new AgentStatusInfo
        {
            Status = AgentStatus.Idle,
            ActiveConversations = this.sessions.GetAll().Count(s => s.IsGenerating),
            CurrentModel = this.defaultModel,
            Uptime = DateTimeOffset.UtcNow,
        }).ConfigureAwait(false);
    }

    private async Task<StreamedTurn> StreamLlmTurnAsync(
        LlmCompletionRequest request,
        TurnResponseDelivery delivery,
        AgentSession session,
        int round,
        string replyConversationId,
        CancellationToken cancellationToken)
    {
        var fullResponse = new StringBuilder();
        var sequenceNumber = 0;
        var toolCallAccumulators = new Dictionary<int, ToolCallAccumulator>();
        LlmTokenUsage? usage = null;

        await foreach (var chunk in this.llmClient.StreamCompleteAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (chunk.ErrorMessage is not null)
            {
                // If the error is a context overflow, try emergency compaction and retry.
                // Only attempt once per turn (LastCompactionRound tracks the last compaction round).
                if (session.LastCompactionRound < 0 && ContextManager.IsContextOverflow(chunk.ErrorMessage))
                {
                    this.LogContextOverflowRetry(session.ConversationId, chunk.ErrorMessage);
                    if (this.memoryExtraction is not null)
                    {
                        this.compaction.FlushExtractionBuffer(session, replyConversationId);
                    }

                    await this.compaction.EmergencyCompactAsync(session, cancellationToken).ConfigureAwait(false);
                    session.LastCompactionRound = round;
                    return new StreamedTurn(StreamOutcome.RetryAfterCompaction, "", [], null, 0);
                }

                await delivery.PersistLlmErrorAsync(chunk.ErrorMessage, cancellationToken).ConfigureAwait(false);
                return new StreamedTurn(StreamOutcome.Errored, "", [], null, 0);
            }

            // Accumulate text content
            if (chunk.ContentDelta is not null)
            {
                fullResponse.Append(chunk.ContentDelta);
                await delivery.StreamChunkAsync(chunk.ContentDelta, sequenceNumber++).ConfigureAwait(false);
            }

            // Accumulate tool call deltas (may be multiple per chunk)
            if (chunk.ToolCallDeltas is { Count: > 0 } deltas)
            {
                foreach (var delta in deltas)
                {
                    if (!toolCallAccumulators.TryGetValue(delta.Index, out var acc))
                    {
                        acc = new ToolCallAccumulator { Index = delta.Index };
                        toolCallAccumulators[delta.Index] = acc;
                    }

                    if (delta.Id is not null)
                    {
                        acc.Id = delta.Id;
                    }

                    if (delta.Name is not null)
                    {
                        acc.Name = delta.Name;
                    }

                    if (delta.ArgumentsDelta is not null)
                    {
                        acc.Arguments.Append(delta.ArgumentsDelta);
                    }
                }
            }

            if (chunk.IsComplete)
            {
                usage = chunk.Usage;
            }
        }

        // Build completed tool calls from accumulators
        var toolCalls = toolCallAccumulators.Values
            .OrderBy(acc => acc.Index)
            .Select(acc => new LlmToolCall
            {
                Id = acc.Id ?? $"call_{acc.Index}",
                Name = acc.Name ?? "unknown",
                Arguments = acc.Arguments.ToString(),
            })
            .ToList();

        return new StreamedTurn(StreamOutcome.Completed, fullResponse.ToString(), toolCalls, usage, sequenceNumber);
    }

    private async Task<ToolRoundOutcome> ExecuteToolRoundAsync(
        AgentSession session,
        TurnResponseDelivery delivery,
        ToolExecutionContext turnContext,
        IReadOnlyList<LlmToolCall> toolCalls,
        string? assistantContent,
        DoomLoopDetector doomLoopDetector,
        int round,
        string replyConversationId,
        CancellationToken cancellationToken)
    {
        // Add the assistant message with tool calls to history
        session.AddMessage(new LlmMessage
        {
            Role = "assistant",
            Content = assistantContent,
            ToolCalls = toolCalls,
        });

        // Per-round tool-call attribution state. roundRecordId stays null on
        // tool-only responses (no text saved); roundToolEntries accumulates as
        // each tool runs in this round.
        var roundToolEntries = new List<Storage.ToolCallSummaryEntry>(toolCalls.Count);

        // Persist the pre-tool text segment so UI history matches what was
        // streamed to the user (voice TTS / text UI). Without this, only the
        // final post-tool text ends up in MessageStore and users see "only
        // the last half" of multi-segment responses.
        long? roundRecordId = await delivery.PersistPreToolTextAsync(assistantContent, cancellationToken).ConfigureAwait(false);

        this.LogToolCallsRequested(session.ConversationId, toolCalls.Count, round + 1);

        // Doom loop detection: if the model keeps making identical tool calls,
        // stop the loop to avoid wasting tokens and API calls.
        if (doomLoopDetector.Check(toolCalls))
        {
            this.LogDoomLoopDetected(session.ConversationId, toolCalls[^1].Name);

            // Add a text response explaining the issue
            var doomMessage = "I appear to be stuck in a loop making the same tool call repeatedly. " +
                "Let me stop and summarize what I've accomplished so far.";
            session.AddMessage(new LlmMessage { Role = "assistant", Content = doomMessage });

            await delivery.DeliverDoomLoopAsync(doomMessage, cancellationToken).ConfigureAwait(false);
            return ToolRoundOutcome.DoomHalted;
        }

        // Execute each tool call and add results to history
        foreach (var toolCall in toolCalls)
        {
            // Notify Bridge: tool started
            await delivery.NotifyToolStartedAsync(toolCall.Name, toolCall.Arguments).ConfigureAwait(false);

            this.LogToolExecuting(session.ConversationId, toolCall.Name, toolCall.Arguments);
            var stopwatch = Stopwatch.StartNew();
            var result = await this.toolRegistry.ExecuteAsync(toolCall, turnContext, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            this.LogToolExecuted(session.ConversationId, toolCall.Name, result.Success, stopwatch.ElapsedMilliseconds);

            // Build the tool result content
            var toolContent = result.Success
                ? result.Content
                : string.Create(CultureInfo.InvariantCulture, $"Error: {result.Error}");

            // Add tool result message to history
            session.AddMessage(new LlmMessage
            {
                Role = "tool",
                Content = toolContent,
                ToolCallId = toolCall.Id,
            });

            // Capture a compact summary entry for this round's tool block.
            roundToolEntries.Add(new Storage.ToolCallSummaryEntry(
                Name: toolCall.Name,
                Args: Storage.ToolCallSummary.TruncateArgs(toolCall.Arguments),
                Ok: result.Success,
                Pos: "after"));

            // Notify Bridge: tool completed/failed
            await delivery.NotifyToolCompletedAsync(
                toolCall.Name, toolCall.Arguments,
                result.Success ? result.Content : result.Error,
                result.Success, stopwatch.Elapsed).ConfigureAwait(false);
        }

        // Attribute this round's tools and flush any patches to the message store.
        // Tool-only responses (roundRecordId == null) buffer entries as "before"
        // for the next text-bearing record without producing a patch yet.
        delivery.RecordRoundTools(roundRecordId, roundToolEntries);
        await delivery.FlushAttributionPatchesAsync(cancellationToken).ConfigureAwait(false);

        // Drain any user messages that arrived during tool execution
        var pendingMessages = session.DrainPendingMessages();
        if (pendingMessages.Count > 0)
        {
            foreach (var pending in pendingMessages)
            {
                var pendingBehavior = MessageSourceBehavior.For(pending.Source);
                var userText = pendingBehavior.PendingInjectionLabelPrefix is { } prefix
                    ? prefix + pending.Text
                    : pending.Text;

                session.AddMessage(new LlmMessage
                {
                    Role = "user",
                    Content = userText,
                    MessageType = pendingBehavior.PendingInjectionMessageType,
                });

                var sourceLabel = pending.Source.ToString();
                this.LogMessageInjectedMidTurn(session.ConversationId, sourceLabel);
            }
        }

        // Compact conversation if context is getting large.
        // Re-compaction is allowed after a cooldown of MinRoundsBetweenCompactions rounds.
        var roundsSinceCompaction = session.LastCompactionRound < 0
            ? int.MaxValue
            : round - session.LastCompactionRound;
        if (roundsSinceCompaction >= MinRoundsBetweenCompactions && this.compaction.ShouldCompact(session))
        {
            if (this.memoryExtraction is not null)
            {
                this.compaction.FlushExtractionBuffer(session, replyConversationId);
            }

            await this.compaction.CompactConversationAsync(session, cancellationToken).ConfigureAwait(false);
            session.LastCompactionRound = round;
        }

        // Loop back for next LLM call with tool results
        return ToolRoundOutcome.Continue;
    }

    /// <summary>
    /// The text recorded into a channel's session history when a proactive /
    /// timer message is delivered there. Recorded verbatim so it reads as a
    /// natural phrase — not wrapped in a "[proactive message sent to …]"
    /// annotation (user feedback 2026-05-15).
    /// </summary>
    internal static string FormatProactiveHistoryEntry(ProactiveMessageRecord proactive)
        => proactive.Text;

    /// <summary>
    /// Replace the session's trailing assistant message with the barge-in
    /// truncated text (already ends with "…"). If the tail is not a plain
    /// assistant message, append a new assistant turn instead.
    /// </summary>
    internal static void TruncateLastAssistantTurn(AgentSession session, string playedText)
    {
        session.ReplaceOrAppendTrailingAssistant(playedText);
    }

    /// <summary>Bridge → Agent barge-in entrypoint.</summary>
    public async Task RecordInterruptedAssistantTurnAsync(string conversationId, string playedText)
    {
        var session = this.sessions.GetOrCreateWithIdleCheck(conversationId);

        // In-memory LLM context: the agent should "remember" only what it said.
        TruncateLastAssistantTurn(session, playedText);

        // Durable history. Mark pending so the persist site writes playedText
        // (case 1: generation still running — the Greg case).
        session.MarkInterrupted(playedText);

        // Case 2: the turn was ALREADY persisted before the interrupt arrived
        // (late barge-in on a short reply) — update the existing row now.
        var recordId = session.LastAssistantRecordId;
        if (recordId != 0)
        {
            await this.messageStore.UpdateContentAsync(
                recordId, playedText, CancellationToken.None).ConfigureAwait(false);
            session.ClearInterruption();
        }

        this.LogTurnInterruptedRecorded(conversationId, playedText.Length);
    }

    /// <summary>Test-only: reach the session for a conversation.</summary>
    internal AgentSession GetOrCreateSessionForTest(string conversationId)
        => this.sessions.GetOrCreateWithIdleCheck(conversationId);

    /// <summary>Accumulates streaming tool call deltas into a complete tool call.</summary>
    private sealed class ToolCallAccumulator
    {
        public required int Index { get; init; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// Fast memory pre-fetch: embeds the latest user message and searches for
    /// semantically similar memories. Returns a formatted string of relevant
    /// memories to inject into the system prompt, or null if none found.
    /// Runs in ~50-100ms (embedding + sqlite-vec query), zero LLM cost.
    /// </summary>


    // ── Image / Multimodal Support ────────────────────────────────────────

    /// <summary>MIME types we accept as images for LLM vision.</summary>
    private static readonly HashSet<string> SupportedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
    };

    /// <summary>Maximum image size in bytes (20 MB — Anthropic's limit).</summary>
    private const long MaxImageBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Builds multimodal content blocks from text + attachments.
    /// Downloads image URLs and converts to base64. Returns null if there are
    /// no image attachments (text-only message).
    /// </summary>
    private async Task<IReadOnlyList<LlmContentBlock>?> BuildContentBlocksAsync(
        string text,
        IReadOnlyList<Contracts.Messages.MediaAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        if (attachments is null or { Count: 0 })
        {
            return null;
        }

        // Filter to supported image types only
        var images = attachments.Where(a => SupportedImageTypes.Contains(a.MimeType)).ToList();
        if (images.Count == 0)
        {
            return null;
        }

        var blocks = new List<LlmContentBlock>();

        // Add image blocks first (providers generally expect images before text)
        using var httpClient = this.httpClientFactory.CreateClient("media-download");

        foreach (var image in images)
        {
            try
            {
                byte[] imageBytes;
                if (image.Data is { Length: > 0 })
                {
                    imageBytes = image.Data;
                }
                else if (!string.IsNullOrEmpty(image.Url))
                {
                    imageBytes = await httpClient.GetByteArrayAsync(image.Url, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    continue;
                }

                if (imageBytes.Length > MaxImageBytes)
                {
                    this.LogImageTooLarge(image.FileName ?? "unknown", imageBytes.Length);
                    continue;
                }

                var base64 = Convert.ToBase64String(imageBytes);
                blocks.Add(LlmContentBlock.ImageBlock(base64, image.MimeType));
                this.LogImageAttached(image.FileName ?? "unknown", image.MimeType, imageBytes.Length);
            }
#pragma warning disable CA1031 // Must not fail the message due to a bad attachment
            catch (Exception ex)
            {
                this.LogImageDownloadFailed(image.FileName ?? image.Url ?? "unknown", ex.Message);
            }
#pragma warning restore CA1031
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        // Add text block after images
        if (!string.IsNullOrEmpty(text))
        {
            blocks.Add(LlmContentBlock.TextBlock(text));
        }

        return blocks;
    }

    // ── Slash Commands ─────────────────────────────────────────────────

    /// <summary>
    /// Synchronous-result counterpart to the text-prefix slash-command path:
    /// dispatches <c>/compact</c> / <c>/context</c> (parameter-less) for a
    /// channel and returns the human-readable response text. Used by the
    /// Bridge to back Discord application slash commands that mirror the
    /// existing text triggers — the Bridge posts the result as the Discord
    /// interaction reply rather than streaming through the message bus.
    /// </summary>
    public async Task<string> RunInlineSlashCommandAsync(
        string channelId, string commandText, CancellationToken cancellationToken)
    {
        var trimmed = (commandText ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return $"Unknown inline command: `{commandText}`. Try `/compact` or `/context`.";
        }

        var session = this.sessions.GetOrCreateWithIdleCheck(channelId);

        // Match `/<word>` exactly so e.g. "/CompactExtra" or "hello /compact" do
        // not accidentally trigger.
        var space = trimmed.IndexOfAny(InlineCommandWordSeparators);
        var word = space < 0 ? trimmed : trimmed[..space];

        if (string.Equals(word, "/compact", StringComparison.OrdinalIgnoreCase))
        {
            return await this.slashCommandHandler.HandleCompactCommandAsync(session, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(word, "/context", StringComparison.OrdinalIgnoreCase))
        {
            return this.slashCommandHandler.HandleContextCommand(session);
        }

        return $"Unknown inline command: `{commandText}`. Try `/compact` or `/context`.";
    }

    /// <summary>
    /// Compact a channel's conversation on demand (flush extraction + LLM summarization).
    /// Called by the admin API to simulate idle compaction.
    /// </summary>
    public async Task<CompactConversationResult> CompactChannelAsync(string channelId, CancellationToken cancellationToken)
    {
        if (!this.sessions.TryGet(channelId, out var session) || session is null)
        {
            return new CompactConversationResult { Success = false, Error = $"No active session for channel '{channelId}'" };
        }

        var beforeCount = session.MessageCount;
        if (beforeCount < 6)
        {
            return new CompactConversationResult { Success = false, Error = $"Not enough messages to compact ({beforeCount}, need at least 6)" };
        }

        if (this.memoryExtraction is not null)
        {
            this.compaction.FlushExtractionBuffer(session, channelId);
            await this.compaction.WaitForExtractionIdleAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }

        await this.compaction.CompactConversationAsync(session, cancellationToken).ConfigureAwait(false);

        return new CompactConversationResult
        {
            Success = true,
            MessagesBefore = beforeCount,
            MessagesAfter = session.MessageCount,
        };
    }

    // ── Conversation Compaction helpers (pure; used by CompactionOrchestrator + tests) ──

    /// <summary>
    /// Splits conversation messages into (messages-to-summarize, preserved-tail).
    /// When history ends with a completed tool round — <c>assistant(tool_calls)</c>
    /// followed by one or more <c>tool</c> messages — the tail is preserved verbatim
    /// after the summary so the model has concrete continuation context.
    /// Otherwise returns <c>(all, [])</c>.
    /// Internal for unit testing; pure function, no side effects.
    /// </summary>
    /// <summary>
    /// Splits the conversation so the most recent <paramref name="preserveTurns"/>
    /// user turns (plus the assistant/tool messages between them and at the end)
    /// are preserved verbatim — provided their combined token count is at or
    /// under <paramref name="budgetTokens"/>. Falls back to
    /// <see cref="SplitAtLastToolRound"/> when:
    /// <list type="bullet">
    ///   <item><c>preserveTurns ≤ 0</c></item>
    ///   <item>the conversation has fewer than <c>preserveTurns + 1</c> user
    ///         turns (no meaningful prefix to summarize)</item>
    ///   <item>the candidate tail exceeds the budget</item>
    /// </list>
    /// Internal for unit testing; pure function, no side effects.
    /// </summary>
    internal static (List<LlmMessage> ToSummarize, List<LlmMessage> PreservedTail) SplitPreservingRecentTurns(
        IReadOnlyList<LlmMessage> messages,
        int preserveTurns,
        int budgetTokens)
    {
        if (preserveTurns <= 0 || messages.Count == 0)
        {
            return SplitAtLastToolRound(messages);
        }

        // Anchor = index of the (preserveTurns)-th user message from the end.
        // Walking backwards counting user roles.
        var anchor = -1;
        var userTurnsSeen = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role != "user")
            {
                continue;
            }

            userTurnsSeen++;
            if (userTurnsSeen == preserveTurns)
            {
                anchor = i;
                break;
            }
        }

        // Need at least one message before the tail to make summarization worth doing.
        if (anchor <= 0)
        {
            return SplitAtLastToolRound(messages);
        }

        var tail = messages.Skip(anchor).ToList();
        var tailTokens = TokenEstimator.EstimateTokens(tail);
        if (tailTokens > budgetTokens)
        {
            return SplitAtLastToolRound(messages);
        }

        var toSummarize = messages.Take(anchor).ToList();
        return (toSummarize, tail);
    }

    internal static (List<LlmMessage> ToSummarize, List<LlmMessage> PreservedTail) SplitAtLastToolRound(IReadOnlyList<LlmMessage> messages)
    {
        // Only preserve when history ends with tool messages.
        if (messages.Count == 0 || messages[^1].Role != "tool")
        {
            return (messages.ToList(), []);
        }

        // Walk back past consecutive tool messages.
        var toolStart = messages.Count - 1;
        while (toolStart > 0 && messages[toolStart - 1].Role == "tool")
        {
            toolStart--;
        }

        // Preceding message must be assistant with tool_calls.
        var assistantIdx = toolStart - 1;
        if (assistantIdx < 0
            || messages[assistantIdx].Role != "assistant"
            || messages[assistantIdx].ToolCalls is not { Count: > 0 })
        {
            // Malformed — don't preserve.
            return (messages.ToList(), []);
        }

        // Guard: don't preserve if it would leave nothing meaningful to summarize.
        if (assistantIdx < 2)
        {
            return (messages.ToList(), []);
        }

        var toSummarize = messages.Take(assistantIdx).ToList();
        var preservedTail = messages.Skip(assistantIdx).ToList();
        return (toSummarize, preservedTail);
    }

    /// <summary>
    /// Wraps a compaction summary in continuation instructions that match the
    /// shape of the post-compaction history. Anti-replay language is included
    /// so the model doesn't re-execute actions already described in the summary.
    /// Internal for unit testing; pure function.
    /// </summary>
    internal static string WrapSummaryForContinuation(string summary, bool hasTail)
    {
        if (hasTail)
        {
            return $$"""
                This session is being continued from a previous conversation that was compacted.
                The summary below covers the earlier part of the conversation; the most recent
                tool call and its results follow verbatim after this message.

                Summary:
                {{summary}}

                Continue the task — respond to the tool results that follow. Do not repeat
                actions that the summary describes as completed.
                """;
        }

        return $$"""
            This session is being continued from a previous conversation that was compacted.
            The summary below covers the earlier part of the conversation.

            Summary:
            {{summary}}

            Continue from where the summary ends. Resume directly — do not acknowledge the
            summary, do not recap what was happening, and do not repeat actions that the
            summary describes as completed. Pick up the next task as if the break never happened.
            """;
    }

    /// <summary>
    /// Returns true when the post-strip history is small enough that running
    /// summarization on top would be counter-productive (it would collapse
    /// freshly-generated verbose image descriptions into a short summary).
    /// Threshold matches <see cref="CompactionOrchestrator.CompactionThreshold"/> — i.e. the same
    /// boundary that triggers proactive compaction in the normal tool loop.
    /// Internal for unit testing; pure function, no side effects.
    /// </summary>
    internal static bool StripAloneSufficient(int strippedTokens, int contextWindow)
    {
        var threshold = (int)(contextWindow * CompactionOrchestrator.CompactionThreshold);
        return strippedTokens < threshold;
    }

    /// <summary>
    /// Appends placeholders for image attachments to the message text so
    /// persisted history reflects that images were present.
    /// </summary>
    private static string AppendAttachmentPlaceholders(string text, IReadOnlyList<Contracts.Messages.MediaAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text);
        foreach (var att in attachments)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"[image attached: {att.MimeType}]");
        }

        return sb.ToString();
    }

    // ── Status & config ────────────────────────────────────────────────

    /// <inheritdoc />
    public Task AbortGenerationAsync(string conversationId)
    {
        if (this.sessions.TryGet(conversationId, out var session) && session is not null)
        {
            session.AbortGeneration();
            this.LogGenerationAborted(conversationId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AgentStatusInfo> GetStatusAsync(CancellationToken cancellationToken)
    {
        var allSessions = this.sessions.GetAll();

        return Task.FromResult(new AgentStatusInfo
        {
            Status = allSessions.Any(s => s.IsGenerating) ? AgentStatus.Processing : AgentStatus.Idle,
            ActiveConversations = allSessions.Count(s => s.IsGenerating),
            CurrentModel = this.defaultModel,
            Uptime = DateTimeOffset.UtcNow,
        });
    }

    /// <inheritdoc />
    public string GetDefaultModel() => this.modelProvider.DefaultModel;

    /// <inheritdoc />
    public void SetDefaultModel(string model, int contextWindow = 128_000, int maxOutputTokens = 8_192)
    {
        this.modelProvider.SetDefaultModel(model, contextWindow, maxOutputTokens);
        this.LogDefaultModelChanged(model, contextWindow, maxOutputTokens);
    }

    /// <inheritdoc />
    public void SetMemoryModel(string? model)
    {
        this.modelProvider.SetMemoryModel(model);
        this.LogMemoryModelChanged(model ?? "(default)");
    }

    /// <inheritdoc />
    public Task UpdateConfigAsync(AgentConfigUpdate config, CancellationToken cancellationToken)
    {
        if (config.SystemPrompt is not null)
        {
            // Write to personality file so it persists and takes effect on next BuildPrompt
            WritePersonality(config.SystemPrompt);
        }

        if (config.MaxTokens.HasValue)
        {
            this.maxTokens = config.MaxTokens.Value;
        }

        if (config.Temperature.HasValue)
        {
            this.temperature = config.Temperature.Value;
        }

        this.LogConfigUpdated(this.defaultModel, this.maxTokens, this.temperature);
        return Task.CompletedTask;
    }

    // ── Session management ─────────────────────────────────────────────

    /// <summary>Compact seeded history when it exceeds this many messages.</summary>
    private const int SeedCompactionThreshold = 20;

    /// <inheritdoc />
    /// <summary>
    /// Per-channel snapshot of the most recent pre-transfer history, used to power
    /// <see cref="RevertTransferAsync"/>. Only the latest snapshot per channel is
    /// retained — a subsequent transfer to the same channel overwrites the prior
    /// snapshot. Snapshots are in-memory only; an agent restart drops them.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<LlmMessage>> transferSnapshots = new();

    /// <inheritdoc/>
    public Task TransferSessionAsync(
        string targetConversationId,
        IReadOnlyList<LlmMessage> seedMessages,
        CancellationToken cancellationToken)
    {
        var targetSession = this.sessions.GetOrCreateWithIdleCheck(targetConversationId);

        // Snapshot pre-transfer history BEFORE drain + seed. Even an empty snapshot
        // is kept so that a revert truly restores "the state before this transfer"
        // (which can be no history at all if the target was fresh).
        this.transferSnapshots[targetConversationId] = targetSession.GetHistory();

        // Drain target's stale extraction buffer — its contents pertain to whatever
        // conversation was happening before the transfer, which is now wiped. Carrying
        // those entries forward would pollute later memory extraction with stale fragments.
        _ = targetSession.DrainExtractionBuffer();

        // Replace target's in-memory history with the seeded payload from the tool.
        // maxHistory: int.MaxValue makes the trim a no-op — we trust the slicer/tool
        // to keep the slice small. (Slicer caps at ~10 messages tail + 1 marker.)
        this.sessions.Seed(targetConversationId, seedMessages, maxHistory: int.MaxValue);
        this.LogTransferSeeded(targetConversationId, seedMessages.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> RevertTransferAsync(string channelId, CancellationToken cancellationToken)
    {
        if (!this.transferSnapshots.TryRemove(channelId, out var snapshot))
        {
            this.LogRevertNoSnapshot(channelId);
            return Task.FromResult(false);
        }

        // Replace the current history with the snapshot, using Seed semantics.
        // Pre-transfer extraction buffer state isn't restored — that buffer is for
        // memory extraction and shouldn't be revived just because the conversation
        // state was rolled back.
        this.sessions.Seed(channelId, snapshot, maxHistory: int.MaxValue);
        this.LogTransferReverted(channelId, snapshot.Count);
        return Task.FromResult(true);
    }

    public async Task SeedHistoryAsync(string channelId, HubChatMessage[] messages, CancellationToken cancellationToken)
    {
        // Skip seeding if the session already has state (restored from snapshot).
        // The snapshot contains the full LLM conversation including tool calls,
        // which is richer than what MessageStore can provide (text only).
        if (this.sessions.TryGet(channelId, out var existingSession)
            && existingSession is not null
            && existingSession.MessageCount > 0)
        {
            this.LogSeedSkipped(channelId, existingSession.MessageCount);
            return;
        }

        var llmMessages = messages.Select(m => new LlmMessage
        {
            Role = m.Role,
            Content = m.Text,
        }).ToList();

        this.sessions.Seed(channelId, llmMessages, this.sessionConfig.MaxHistory);
        this.LogHistorySeeded(channelId, messages.Length);

        // Compact seeded history if it's large enough. This replaces older messages
        // with a summary, keeping the most recent exchanges for immediate context.
        // Important details are already preserved in long-term memory.
        if (messages.Length >= SeedCompactionThreshold)
        {
            if (this.sessions.TryGet(channelId, out var session) && session is not null)
            {
                if (this.memoryExtraction is not null)
                {
                    this.compaction.FlushExtractionBuffer(session, channelId);
                }

                await this.compaction.CompactConversationAsync(session, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public Task ResetSessionAsync(string channelId, CancellationToken cancellationToken)
    {
        this.sessions.Reset(channelId);
        // Also drop any transfer snapshot for this channel — if the user is clearing
        // the channel they explicitly don't want the pre-transfer history coming back
        // via a later revert_transfer call.
        this.transferSnapshots.TryRemove(channelId, out _);
        this.LogSessionReset(channelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetAllSessionsAsync(CancellationToken cancellationToken)
    {
        this.sessions.ResetAll();
        this.transferSnapshots.Clear();
        this.LogAllSessionsReset();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetActiveChannels(string[] channelIds)
    {
        this.activeChannelStore.Set(channelIds);
        var channelList = string.Join(", ", channelIds);
        this.LogActiveChannelsSet(channelIds.Length, channelList);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetActiveChannels() => this.activeChannelStore.Get();

    // ── Personality management ───────────────────────────────────────────

    /// <inheritdoc />
    public Task<string> GetPersonalityAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(LoadPersonality());
    }

    /// <inheritdoc />
    /// <summary>Maximum character count for personality (~1000 tokens).</summary>
    private const int MaxPersonalityCharacters = 4000;

    public async Task SetPersonalityAsync(string personality, CancellationToken cancellationToken)
    {
        if (personality.Length > MaxPersonalityCharacters)
        {
            personality = personality[..MaxPersonalityCharacters];
        }

        WritePersonality(personality);

        // Detect voice gender from personality via LLM and report to Bridge
        _ = Task.Run(async () =>
        {
            try
            {
                var gender = await this.DetectVoiceGenderAsync(personality, cancellationToken).ConfigureAwait(false);
                var client = this.bridgeClientAccessor.Client;
                if (client is not null)
                {
                    await client.OnVoiceGenderDetected(gender).ConfigureAwait(false);
                    this.LogVoiceGenderDetected(gender);
                }
            }
            catch (Exception ex)
            {
                this.LogVoiceGenderDetectionFailed(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Detects voice gender from a personality description using a cheap LLM call.
    /// Returns "male" or "female".
    /// </summary>
    private async Task<string> DetectVoiceGenderAsync(string personality, CancellationToken cancellationToken)
    {
        var request = new LlmCompletionRequest
        {
            Model = this.modelProvider.MemoryModel ?? this.defaultModel,
            Messages =
            [
                new LlmMessage
                {
                    Role = "system",
                    Content = "You are a classifier. Given a personality description for an AI assistant, determine the voice gender. Reply with exactly one word: 'male' or 'female'. If uncertain, reply 'female'.",
                },
                new LlmMessage
                {
                    Role = "user",
                    Content = personality,
                },
            ],
            Temperature = 0,
            MaxTokens = TokenLimits.Tiny,
            RequestId = $"gender-detect-{Guid.NewGuid():N}",
            ConversationId = "system",
        };

        var result = await this.llmClient.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = result.Content?.Trim().ToLowerInvariant() ?? "female";

        return response.Contains("male", StringComparison.OrdinalIgnoreCase)
            && !response.Contains("female", StringComparison.OrdinalIgnoreCase)
            ? "male"
            : "female";
    }

    /// <summary>
    /// Reads personality.md from the sandbox. Returns <see cref="DefaultPersonality"/>
    /// if the file does not exist.
    /// </summary>
    private string LoadPersonality()
    {
        try
        {
            if (File.Exists(this.personalityPath))
            {
                var writeUtc = File.GetLastWriteTimeUtc(this.personalityPath);
                lock (this.personalityCacheLock)
                {
                    if (this.cachedPersonality is not null && this.cachedPersonalityWriteUtc == writeUtc)
                    {
                        return this.cachedPersonality;
                    }
                }

                var content = File.ReadAllText(this.personalityPath).Trim();
                if (content.Length > 0)
                {
                    lock (this.personalityCacheLock)
                    {
                        this.cachedPersonality = content;
                        this.cachedPersonalityWriteUtc = writeUtc;
                    }

                    return content;
                }
            }
        }
        catch (IOException ex)
        {
            this.LogPersonalityReadFailed(this.personalityPath, ex);
        }

        return DefaultPersonality;
    }

    /// <summary>
    /// Writes personality.md to the sandbox, creating parent directories if needed.
    /// </summary>
    private void WritePersonality(string personality)
    {
        try
        {
            var dir = Path.GetDirectoryName(this.personalityPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(this.personalityPath, personality);

            // Invalidate the read cache so the next LoadPersonality picks up the new content.
            lock (this.personalityCacheLock)
            {
                this.cachedPersonality = null;
            }
        }
        catch (IOException ex)
        {
            this.LogPersonalityWriteFailed(this.personalityPath, ex);
        }
    }

    // ── Bootstrap context management ─────────────────────────────────────

    /// <summary>
    /// Reads context-bootstrap.md from outside the sandbox.
    /// Returns <see cref="DefaultBootstrapContext"/> if the file does not exist.
    /// </summary>
    private string LoadBootstrapContext()
    {
        try
        {
            var path = Path.GetFullPath(this.bootstrapContextPath);
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path).Trim();
                if (content.Length > 0)
                {
                    return content;
                }
            }
        }
        catch (IOException ex)
        {
            this.LogBootstrapContextReadFailed(ex);
        }

        return DefaultBootstrapContext;
    }

    /// <summary>
    /// Gets the full resolved path to context-bootstrap.md.
    /// Used by the context bootstrap tool.
    /// </summary>
    internal string GetBootstrapContextPath() => Path.GetFullPath(this.bootstrapContextPath);

    /// <summary>
    /// Gets the current bootstrap context content.
    /// </summary>
    public Task<string> GetBootstrapContextAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(LoadBootstrapContext());
    }

    /// <summary>
    /// Sets the bootstrap context content (writes context-bootstrap.md).
    /// </summary>
    public Task SetBootstrapContextAsync(string content, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.GetFullPath(this.bootstrapContextPath);
            var dir = Path.GetDirectoryName(path);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, content);
        }
        catch (IOException ex)
        {
            this.LogBootstrapContextWriteFailed(ex);
        }

        return Task.CompletedTask;
    }

    // ── LoggerMessage source-generated methods ───────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Message enqueued for {ConversationId}: correlationId={CorrelationId}")]
    private partial void LogMessageEnqueued(string conversationId, string correlationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer loop started")]
    private partial void LogConsumerStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer loop stopped")]
    private partial void LogConsumerStopped();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dispatched message {ConversationId} (correlationId={CorrelationId}) to lane {DispatchKey}")]
    private partial void LogMessageDispatched(string conversationId, string correlationId, string dispatchKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer error processing message for {ConversationId} (correlationId={CorrelationId}): {ErrorMessage}")]
    private partial void LogConsumerMessageError(string conversationId, string correlationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bridge not connected for conversation {ConversationId}, cannot generate response")]
    private partial void LogBridgeNotConnected(string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool calls requested for {ConversationId}: count={ToolCallCount}, round={Round}")]
    private partial void LogToolCallsRequested(string conversationId, int toolCallCount, int round);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generation cancelled for {ConversationId}")]
    private partial void LogGenerationCancelled(string conversationId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Generation error for {ConversationId}: {ErrorMessage}")]
    private partial void LogGenerationError(string conversationId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Generation aborted for {ConversationId}")]
    private partial void LogGenerationAborted(string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Default model changed to {DefaultModel} (context={ContextWindow}, maxOutput={MaxOutputTokens})")]
    private partial void LogDefaultModelChanged(string defaultModel, int contextWindow, int maxOutputTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory model changed to {MemoryModel}")]
    private partial void LogMemoryModelChanged(string memoryModel);

    [LoggerMessage(Level = LogLevel.Information, Message = "Config updated: model={DefaultModel}, maxTokens={MaxTokens}, temperature={Temperature}")]
    private partial void LogConfigUpdated(string defaultModel, int maxTokens, double temperature);

    [LoggerMessage(Level = LogLevel.Information, Message = "History seeded for channel {ChannelId}: {MessageCount} messages")]
    private partial void LogHistorySeeded(string channelId, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed skipped for channel {ChannelId}: session already has {MessageCount} messages (restored from snapshot)")]
    private partial void LogSeedSkipped(string channelId, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transfer-seeded conversation {ConversationId}: {MessageCount} messages")]
    private partial void LogTransferSeeded(string conversationId, int messageCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reverted transfer for channel {ChannelId}: restored {MessageCount} messages from pre-transfer snapshot")]
    private partial void LogTransferReverted(string channelId, int messageCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Revert requested for channel {ChannelId} but no transfer snapshot is available")]
    private partial void LogRevertNoSnapshot(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Empty message rejected for {ConversationId} on channel {ChannelId} (no text and no attachments)")]
    private partial void LogEmptyMessageRejected(string conversationId, string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice gender detected from personality: {Gender}")]
    private partial void LogVoiceGenderDetected(string gender);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voice gender detection failed: {Error}")]
    private partial void LogVoiceGenderDetectionFailed(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session reset for channel {ChannelId}")]
    private partial void LogSessionReset(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "All sessions reset")]
    private partial void LogAllSessionsReset();

    [LoggerMessage(Level = LogLevel.Information, Message = "Executing tool {ToolName} for {ConversationId}: {ToolInput}")]
    private partial void LogToolExecuting(string conversationId, string toolName, string toolInput);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tool {ToolName} completed for {ConversationId}: success={Success}, duration={DurationMs}ms")]
    private partial void LogToolExecuted(string conversationId, string toolName, bool success, long durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read personality file at {PersonalityPath}, using default")]
    private partial void LogPersonalityReadFailed(string personalityPath, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write personality file at {PersonalityPath}")]
    private partial void LogPersonalityWriteFailed(string personalityPath, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read context-bootstrap.md, using default")]
    private partial void LogBootstrapContextReadFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write context-bootstrap.md")]
    private partial void LogBootstrapContextWriteFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Active channels set ({Count}): {ChannelList}")]
    private partial void LogActiveChannelsSet(int count, string channelList);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image attached: {FileName}, type={MimeType}, size={SizeBytes}")]
    private partial void LogImageAttached(string fileName, string mimeType, int sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image too large, skipping: {FileName}, size={SizeBytes}")]
    private partial void LogImageTooLarge(string fileName, int sizeBytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download image {ImageRef}: {ErrorMessage}")]
    private partial void LogImageDownloadFailed(string imageRef, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "[tokens] {ConversationId} round={Round} promptTokens={PromptTokens} completionTokens={CompletionTokens} totalTokens={TotalTokens} sessionMessages={SessionMessages} cacheWrite={CacheWrite} cacheRead={CacheRead}")]
    private partial void LogTokenUsage(string conversationId, int round, int promptTokens, int completionTokens, int totalTokens, int sessionMessages, int cacheWrite, int cacheRead);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Context overflow detected for {ConversationId}, attempting emergency compaction. LLM error: {ErrorMessage}")]
    private partial void LogContextOverflowRetry(string conversationId, string errorMessage);

    // ── Subagent completion handling ────────────────────────────────────

    /// <summary>
    /// Called when a background subagent completes. Makes a dedicated LLM call to evaluate
    /// the result quality and either deliver a curated response to the user or resume the
    /// subagent with steering instructions.
    /// </summary>
    internal Task ProcessSubagentCompletionAsync(string taskId, string result)
    {
        if (this.subagentStore is null)
        {
            return Task.CompletedTask;
        }

        var task = this.subagentStore.GetById(taskId);
        if (task is null)
        {
            this.LogSubagentCompletionTaskNotFound(taskId);
            return Task.CompletedTask;
        }

        // Store the result
        this.subagentStore.UpdateState(taskId, SubagentTaskState.Completed, result: result);

        // Enqueue a completion notification into the parent conversation.
        // The main agent processes it through the full tool loop and decides
        // what to do: respond to user, start a follow-up task, or both.
        var triggerText =
            $"[Background task completed]\n" +
            $"Task: \"{task.Description}\" ({task.TaskId})\n\n" +
            $"Result:\n{result}\n\n" +
            $"Review the result and respond to the user. " +
            $"If there is a follow-up task to do, use sub_agent_start. " +
            $"Use sub_agent_read('{task.TaskId}') if you need more details.";

        this.messageQueue.TryEnqueue(new AgentMessage
        {
            ConversationId = task.ParentConversation,
            ChannelId = task.ParentChannel,
            Text = triggerText,
            Source = AgentMessageSource.SubagentCompletion,
        });

        this.LogSubagentCompletionEnqueued(taskId, task.ParentConversation);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[subagent-completion] Task {TaskId} result enqueued for processing on {ConversationId}")]
    private partial void LogSubagentCompletionEnqueued(string taskId, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[subagent-completion] Task {TaskId} not found in store")]
    private partial void LogSubagentCompletionTaskNotFound(string taskId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Idle compaction performed for {ConversationId}: conversation summarized instead of wiped")]
    private partial void LogIdleCompactionPerformed(string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Proactive message injected into {ChannelId} session ({Length} chars)")]
    private partial void LogProactiveMessageInjected(string channelId, int length);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice: recorded barge-in truncation for {ConversationId} ({Chars} chars)")]
    private partial void LogTurnInterruptedRecorded(string conversationId, int chars);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Doom loop detected for {ConversationId}: tool '{ToolName}' called with identical arguments 3+ times consecutively")]
    private partial void LogDoomLoopDetected(string conversationId, string toolName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mid-turn message injected for {ConversationId}: source={Source}")]
    private partial void LogMessageInjectedMidTurn(string conversationId, string source);
}

