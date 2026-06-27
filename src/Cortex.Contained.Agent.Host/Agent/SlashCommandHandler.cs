using System.Globalization;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Handles local slash commands (<c>/context</c>, <c>/compact</c>) that are
/// intercepted before the LLM pipeline. Extracted from <see cref="AgentRuntime"/>
/// as a move-only refactor: semantics, output text, and persistence are preserved
/// exactly. Delegates the actual compaction work to
/// <see cref="CompactionOrchestrator"/>.
/// </summary>
internal sealed class SlashCommandHandler
{
    private readonly BridgeClientAccessor bridgeClientAccessor;
    private readonly Storage.IMessageStore messageStore;
    private readonly IModelProvider modelProvider;
    private readonly CompactionOrchestrator compaction;

    public SlashCommandHandler(
        BridgeClientAccessor bridgeClientAccessor,
        Storage.IMessageStore messageStore,
        IModelProvider modelProvider,
        CompactionOrchestrator compaction)
    {
        this.bridgeClientAccessor = bridgeClientAccessor;
        this.messageStore = messageStore;
        this.modelProvider = modelProvider;
        this.compaction = compaction;
    }

    /// <summary>
    /// Fallback context window when the model's actual limit is unknown.
    /// </summary>
    private const int FallbackContextWindow = 128_000;

    /// <summary>
    /// Handles local slash commands (/context, /compact) without entering the LLM pipeline.
    /// Sends a synthetic response back to the user via the Bridge.
    /// </summary>
    public async Task HandleSlashCommandAsync(
        string commandText, AgentSession session, AgentMessage message, CancellationToken cancellationToken)
    {
        var client = this.bridgeClientAccessor.Client;
        if (client is null)
        {
            return;
        }

        string responseText;

        if (commandText.StartsWith("/compact", StringComparison.OrdinalIgnoreCase))
        {
            responseText = await HandleCompactCommandAsync(session, cancellationToken).ConfigureAwait(false);
        }
        else // /context
        {
            responseText = HandleContextCommand(session);
        }

        var messageId = Guid.NewGuid().ToString("N");

        // Send the response as a single chunk + completion, mimicking a normal LLM response
        await client.OnResponseChunk(new ResponseChunkMessage
        {
            ConversationId = message.ConversationId,
            Text = responseText,
            SequenceNumber = 0,
            IsComplete = false,
            CorrelationId = message.CorrelationId,
        }).ConfigureAwait(false);

        await client.OnResponseChunk(new ResponseChunkMessage
        {
            ConversationId = message.ConversationId,
            Text = string.Empty,
            SequenceNumber = 1,
            IsComplete = true,
            CorrelationId = message.CorrelationId,
        }).ConfigureAwait(false);

        await client.OnResponseComplete(new ResponseCompleteMessage
        {
            ConversationId = message.ConversationId,
            MessageId = messageId,
            FullText = responseText,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = message.CorrelationId,
            Category = MessageCategory.System, // Visible in chat UI but excluded from seeding
        }).ConfigureAwait(false);

        // Persist slash command response to local MessageStore.
        // Persist the slash command input as a System user message
        await this.messageStore.SaveMessageAsync(
            userId: message.SenderIdHash ?? "unknown",
            channelId: message.ChannelId,
            role: "user",
            content: message.Text,
            timestamp: message.Timestamp,
            category: MessageCategory.System,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Persist the slash command response
        await this.messageStore.SaveMessageAsync(
            userId: "assistant",
            channelId: message.ChannelId,
            role: "assistant",
            content: responseText,
            timestamp: DateTimeOffset.UtcNow,
            messageId: messageId,
            category: MessageCategory.System,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// /context — Shows current context window usage: prompt tokens, context window size,
    /// percentage used, message count, and model info.
    /// </summary>
    public string HandleContextCommand(AgentSession session)
    {
        var contextWindow = this.modelProvider.ContextWindow > 0 ? this.modelProvider.ContextWindow : FallbackContextWindow;
        var maxOutput = TokenLimits.ResolveMaxOutput(this.modelProvider);
        var promptTokens = session.LastPromptTokens;
        var messageCount = session.MessageCount;

        // Estimate tokens from current history if no API-reported usage yet
        if (promptTokens <= 0)
        {
            var history = session.GetHistory();
            promptTokens = TokenEstimator.EstimateTokens(history);
        }

        var usableWindow = contextWindow - maxOutput;
        var percentUsed = usableWindow > 0 ? (double)promptTokens / usableWindow * 100 : 0;
        var compactionThresholdTokens = (int)(contextWindow * CompactionOrchestrator.CompactionThreshold);

        return string.Create(CultureInfo.InvariantCulture, $"""
            **Context Window**
            Model: {this.modelProvider.DefaultModel}
            Context window: {contextWindow:N0} tokens
            Max output: {maxOutput:N0} tokens
            Usable for input: {usableWindow:N0} tokens

            **Current Usage**
            Prompt tokens: {promptTokens:N0} ({percentUsed:F1}% of usable)
            Session messages: {messageCount}
            Compaction threshold: {compactionThresholdTokens:N0} tokens ({CompactionOrchestrator.CompactionThreshold:P0} of window)
            Last compaction round: {(session.LastCompactionRound < 0 ? "none" : session.LastCompactionRound.ToString(CultureInfo.InvariantCulture))}
            """);
    }

    /// <summary>
    /// /compact — Triggers manual compaction and reports before/after stats.
    /// </summary>
    public async Task<string> HandleCompactCommandAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var beforeCount = session.MessageCount;
        var beforeTokens = TokenEstimator.EstimateTokens(session.GetHistory());

        if (beforeCount < 6)
        {
            return $"Not enough messages to compact (have {beforeCount}, need at least 6).";
        }

        if (this.compaction.HasMemoryExtraction)
        {
            this.compaction.FlushExtractionBuffer(session, session.ConversationId);
        }

        await this.compaction.CompactConversationAsync(session, cancellationToken).ConfigureAwait(false);

        var afterCount = session.MessageCount;
        var afterTokens = TokenEstimator.EstimateTokens(session.GetHistory());

        return string.Create(CultureInfo.InvariantCulture, $"""
            **Compaction Complete**
            Messages: {beforeCount} → {afterCount}
            Estimated tokens: {beforeTokens:N0} → {afterTokens:N0}
            Saved: ~{beforeTokens - afterTokens:N0} tokens
            """);
    }
}
