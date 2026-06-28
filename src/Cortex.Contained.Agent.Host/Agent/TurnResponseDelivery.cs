using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Per-turn helper that emits a turn's response to the Bridge and persists it to
/// <see cref="MessageStore"/> — streaming chunks, tool-execution notifications, the final
/// response (proactive vs streaming), doom-loop output, LLM errors, tool-call attribution,
/// and barge-in reconciliation. One instance per <c>GenerateResponseAsync</c> invocation;
/// owns its own <see cref="ToolCallAttributor"/>. The tool-round loop, session-history
/// mutation, and compaction stay in <see cref="AgentRuntime"/>.
/// </summary>
/// <remarks>
/// The pure Bridge-notify methods (<see cref="StreamChunkAsync"/>,
/// <see cref="NotifyToolStartedAsync"/>, <see cref="NotifyToolCompletedAsync"/>) intentionally
/// take no <see cref="CancellationToken"/> — the underlying <see cref="IAgentHubClient"/> calls
/// accept none, so there would be nothing to honour. The persistence methods take one because
/// their <see cref="MessageStore"/> writes do observe it.
/// </remarks>
internal sealed partial class TurnResponseDelivery
{
    private const int DoomLoopSequenceNumber = 0;

    private readonly IAgentHubClient client;
    private readonly IMessageStore messageStore;
    private readonly string replyConversationId;
    private readonly string channelId;
    private readonly string correlationId;
    private readonly bool useProactiveDelivery;
    private readonly ILogger logger;
    private readonly ToolCallAttributor attributor = new();

    public TurnResponseDelivery(
        IAgentHubClient client,
        IMessageStore messageStore,
        string replyConversationId,
        string channelId,
        string correlationId,
        bool useProactiveDelivery,
        ILogger logger)
    {
        this.client = client;
        this.messageStore = messageStore;
        this.replyConversationId = replyConversationId;
        this.channelId = channelId;
        this.correlationId = correlationId;
        this.useProactiveDelivery = useProactiveDelivery;
        this.logger = logger;
    }

    public async Task StreamChunkAsync(string text, int sequenceNumber)
    {
        if (this.useProactiveDelivery)
        {
            return;
        }

        await this.client.OnResponseChunk(new ResponseChunkMessage
        {
            ConversationId = this.replyConversationId,
            Text = text,
            SequenceNumber = sequenceNumber,
            IsComplete = false,
            CorrelationId = this.correlationId,
        }).ConfigureAwait(false);
    }

    public async Task PersistLlmErrorAsync(string errorMessage, CancellationToken cancellationToken)
    {
        // The raw message may carry a full HTML error page (e.g. GitHub's "Unicorn!" 502);
        // it is already in the logs. Present a clean line to the channel + history.
        var userMessage = Llm.LlmErrorPresenter.ToUserMessage(errorMessage);

        await this.client.OnError(new AgentErrorMessage
        {
            ConversationId = this.replyConversationId,
            ErrorCode = ErrorCodes.LlmError,
            Message = userMessage,
            IsRetryable = true,
            CorrelationId = this.correlationId,
        }).ConfigureAwait(false);

        await this.messageStore.SaveMessageAsync(
            userId: "assistant",
            channelId: this.channelId,
            role: "assistant",
            content: $"LLM Error: {userMessage}",
            timestamp: DateTimeOffset.UtcNow,
            category: MessageCategory.System,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers AND persists a pre-tool text segment (text the model emitted before a
    /// tool call, e.g. "Let me check the log first."). Persistence makes it appear in
    /// history; the <see cref="IAgentHubClient.OnResponseComplete"/> call finalizes the
    /// segment so finalize-only channels (e.g. Discord DM, whose live streaming is a
    /// no-op) actually post it — previously these segments were persisted but never
    /// delivered, so they showed in history/web but never reached Discord.
    /// <para>
    /// Scheduled-task (proactive) turns never auto-deliver to user channels — they only
    /// persist — matching <see cref="DeliverFinalResponseAsync"/>.
    /// </para>
    /// </summary>
    public async Task<long?> DeliverPreToolTextAsync(string? assistantContent, CancellationToken cancellationToken)
    {
        if (assistantContent is not { Length: > 0 })
        {
            return null;
        }

        var messageId = Guid.NewGuid().ToString("N");

        if (!this.useProactiveDelivery)
        {
            // Finalize this segment as its own message. The matching live stream chunks
            // were already emitted during generation; this closes the segment so the
            // Bridge delivers it (Discord POST / web-UI finalize) instead of dropping it.
            await this.client.OnResponseComplete(new ResponseCompleteMessage
            {
                ConversationId = this.replyConversationId,
                MessageId = messageId,
                FullText = assistantContent,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = this.correlationId,
            }).ConfigureAwait(false);
        }

        return await this.messageStore.SaveMessageAsync(
            userId: "assistant",
            channelId: this.channelId,
            role: "assistant",
            content: assistantContent,
            timestamp: DateTimeOffset.UtcNow,
            messageId: messageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task NotifyToolStartedAsync(string toolName, string input)
    {
        await this.client.OnToolExecution(new ToolExecutionMessage
        {
            ConversationId = this.replyConversationId,
            ToolName = toolName,
            Status = ToolExecutionStatus.Started,
            Input = input,
            CorrelationId = this.correlationId,
        }).ConfigureAwait(false);
    }

    public async Task NotifyToolCompletedAsync(
        string toolName, string input, string? output, bool success, TimeSpan duration)
    {
        await this.client.OnToolExecution(new ToolExecutionMessage
        {
            ConversationId = this.replyConversationId,
            ToolName = toolName,
            Status = success ? ToolExecutionStatus.Completed : ToolExecutionStatus.Failed,
            Input = input,
            Output = output,
            Duration = duration,
            CorrelationId = this.correlationId,
        }).ConfigureAwait(false);
    }

    public void RecordRoundTools(long? recordId, IReadOnlyList<ToolCallSummaryEntry> entries)
        => this.attributor.RecordResponseTools(recordId, entries);

    public async Task FlushAttributionPatchesAsync(CancellationToken cancellationToken)
    {
        foreach (var patch in this.attributor.DrainPatches())
        {
            var json = ToolCallSummary.SerializeJson(patch.Entries);
            await this.messageStore.UpdateToolCallsAsync(patch.MessageId, json, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeliverDoomLoopAsync(string doomMessage, CancellationToken cancellationToken)
    {
        await this.client.OnResponseChunk(new ResponseChunkMessage
        {
            ConversationId = this.replyConversationId,
            Text = doomMessage,
            SequenceNumber = DoomLoopSequenceNumber, // single self-contained chunk — no streaming precedes the doom-loop message
            IsComplete = true,
            CorrelationId = this.correlationId,
        }).ConfigureAwait(false);

        var doomMessageId = Guid.NewGuid().ToString("N");
        await this.client.OnResponseComplete(new ResponseCompleteMessage
        {
            ConversationId = this.replyConversationId,
            MessageId = doomMessageId,
            FullText = doomMessage,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = this.correlationId,
        }).ConfigureAwait(false);

        await this.messageStore.SaveMessageAsync(
            userId: "assistant",
            channelId: this.channelId,
            role: "assistant",
            content: doomMessage,
            timestamp: DateTimeOffset.UtcNow,
            messageId: doomMessageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task DeliverFinalResponseAsync(
        AgentSession session,
        string responseText,
        string? instructionText,
        LlmTokenUsage? usage,
        int sequenceNumber,
        string messageId,
        CancellationToken cancellationToken)
    {
        if (this.useProactiveDelivery)
        {
            // Scheduled tasks: persist instruction + response to the scheduled-tasks
            // channel via OnScheduledTaskComplete. The Bridge saves both to SQLite.
            // The agent does NOT auto-deliver to user channels — the send_message
            // tool is the sole mechanism for channel delivery.
            await this.client.OnScheduledTaskComplete(new ScheduledTaskCompleteMessage
            {
                TaskId = this.replyConversationId,
                InstructionText = instructionText ?? string.Empty,
                ResponseText = responseText,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = this.correlationId,
            }).ConfigureAwait(false);

            this.LogResponseComplete(session.ConversationId, messageId, responseText.Length,
                usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0, usage?.TotalTokens ?? 0);
        }
        else
        {
            // User messages: deliver via streaming chunks + completion signal.
            await this.client.OnResponseChunk(new ResponseChunkMessage
            {
                ConversationId = this.replyConversationId,
                Text = string.Empty,
                SequenceNumber = sequenceNumber,
                IsComplete = true,
                CorrelationId = this.correlationId,
            }).ConfigureAwait(false);

            await this.client.OnResponseComplete(new ResponseCompleteMessage
            {
                ConversationId = this.replyConversationId,
                MessageId = messageId,
                FullText = responseText,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = this.correlationId,
                Usage = usage is not null
                    ? new TokenUsage
                    {
                        PromptTokens = usage.PromptTokens,
                        CompletionTokens = usage.CompletionTokens,
                        TotalTokens = usage.TotalTokens,
                    }
                    : null,
            }).ConfigureAwait(false);

            this.LogResponseComplete(session.ConversationId, messageId, responseText.Length,
                usage?.PromptTokens ?? 0, usage?.CompletionTokens ?? 0, usage?.TotalTokens ?? 0);
        }

        // Persist assistant response to local MessageStore. The final response
        // has no tool calls of its own (we're in the no-tool-calls exit branch),
        // but any orphan tools buffered from a preceding tool-only LLM round
        // attach as "before" via the attributor.
        // Persist what was actually spoken if this turn was barge-in
        // interrupted (the in-flight interrupt may have set the marker
        // before generation finished — the Greg case).
        var interruptedBeforeSave = session.InterruptedPlayedText;
        var contentToPersist = ChooseAssistantContent(session, responseText);

        var finalRecordId = await this.messageStore.SaveMessageAsync(
            userId: "assistant",
            channelId: this.channelId,
            role: "assistant",
            content: contentToPersist,
            timestamp: DateTimeOffset.UtcNow,
            messageId: messageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        session.SetLastAssistantRecordId(finalRecordId);

        // Reconcile: an interrupt's MarkInterrupted can land during the
        // await SaveMessageAsync above (interrupt runs on the hub
        // thread, persist on the session loop) — it saw recordId==0,
        // left the marker pending, and we just saved the FULL text.
        // CancellationToken.None: the generation token is cancelled by
        // the barge-in, but this fix-up MUST run or the interrupted
        // turn re-leaks full text (see the barge-in truncation path in AgentRuntime).
        var interruptedDuringSave = session.InterruptedPlayedText;
        if (interruptedBeforeSave is null && interruptedDuringSave is not null)
        {
            await this.messageStore.UpdateContentAsync(
                finalRecordId, interruptedDuringSave, CancellationToken.None).ConfigureAwait(false);
        }

        if (interruptedBeforeSave is not null || interruptedDuringSave is not null)
        {
            session.ClearInterruption();
        }

        this.attributor.RecordResponseTools(finalRecordId, []);
        foreach (var patch in this.attributor.DrainPatches())
        {
            var json = ToolCallSummary.SerializeJson(patch.Entries);
            await this.messageStore.UpdateToolCallsAsync(patch.MessageId, json, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The content to persist for an assistant turn: the barge-in spoken text when the turn
    /// was interrupted, otherwise the full response text.
    /// </summary>
    private static string ChooseAssistantContent(AgentSession session, string responseText)
        => session.InterruptedPlayedText ?? responseText;

    [LoggerMessage(EventId = 9210, Level = LogLevel.Information,
        Message = "Response complete for {ConversationId}: messageId={MessageId}, length={ResponseLength}, promptTokens={PromptTokens}, completionTokens={CompletionTokens}, totalTokens={TotalTokens}")]
    private partial void LogResponseComplete(string conversationId, string messageId, int responseLength, int promptTokens, int completionTokens, int totalTokens);
}
