using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Production implementation of <see cref="IProactiveMessageDispatcher"/>.
/// Forwards to the Bridge via <see cref="BridgeClientAccessor"/>, then persists
/// to <see cref="MessageStore"/> and records to the tool-execution context so
/// AgentRuntime can inject the message into the target session's history after
/// the tool loop completes.
/// </summary>
internal sealed partial class ProactiveMessageDispatcher : IProactiveMessageDispatcher
{
    private readonly BridgeClientAccessor bridgeClientAccessor;
    private readonly MessageStore messageStore;
    private readonly ILogger<ProactiveMessageDispatcher> logger;

    public ProactiveMessageDispatcher(
        BridgeClientAccessor bridgeClientAccessor,
        MessageStore messageStore,
        ILogger<ProactiveMessageDispatcher> logger)
    {
        this.bridgeClientAccessor = bridgeClientAccessor;
        this.messageStore = messageStore;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ProactiveDispatchResult> DispatchAsync(
        string channelId,
        string text,
        ToolExecutionContext? context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return new ProactiveDispatchResult { Success = false, Error = "channelId is required." };
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new ProactiveDispatchResult { Success = false, Error = "text is required." };
        }

        var client = this.bridgeClientAccessor.Client;
        if (client is null)
        {
            return new ProactiveDispatchResult { Success = false, Error = "Bridge is not connected. Cannot send proactive messages." };
        }

        var proactiveMessage = new ProactiveMessage
        {
            Text = text,
            ChannelId = channelId,
            CorrelationId = context?.CorrelationId ?? Guid.NewGuid().ToString("N"),
        };

        ProactiveMessageResult bridgeResult;
        try
        {
            bridgeResult = await client.OnProactiveMessage(proactiveMessage).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Bridge call failures must surface as ProactiveDispatchResult.Failure, not crash the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Truncate provider exception text — deep SignalR/transport failures can include
            // stack-like detail that would leak into agent-visible result content otherwise.
            var safeReason = Truncate(ex.Message);
            this.LogDispatchFailed(channelId, safeReason);
            return new ProactiveDispatchResult { Success = false, Error = $"Failed to send message: {safeReason}" };
        }

        if (!bridgeResult.Success)
        {
            var safeReason = Truncate(bridgeResult.Error ?? "(unspecified)");
            this.LogDispatchFailed(channelId, safeReason);
            return new ProactiveDispatchResult
            {
                Success = false,
                Error = $"Failed to send message: {safeReason}",
            };
        }

        // Record for deferred injection into the target channel's session history.
        // AgentRuntime drains context.ProactiveMessages after the tool loop and
        // appends via AppendOrGlueAssistantMessage. Caller may pass null in flows
        // outside the tool loop (e.g. tests).
        context?.ProactiveMessages.Add(new ProactiveMessageRecord
        {
            ChannelId = channelId,
            Text = text,
        });

        // Persist for Bridge-side history (the chat UI). Forwards the caller's
        // cancellation token so a cooperative cancellation aborts the persist.
        await this.messageStore.SaveMessageAsync(
            userId: "assistant",
            channelId: channelId,
            role: "assistant",
            content: text,
            timestamp: DateTimeOffset.UtcNow,
            category: MessageCategory.Proactive,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        this.LogDispatchSucceeded(channelId, bridgeResult.ConversationId);
        return new ProactiveDispatchResult
        {
            Success = true,
            ConversationId = bridgeResult.ConversationId,
        };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "proactive_dispatch succeeded: channel={ChannelId} conversation={ConversationId}")]
    private partial void LogDispatchSucceeded(string channelId, string? conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "proactive_dispatch failed: channel={ChannelId} reason={Reason}")]
    private partial void LogDispatchFailed(string channelId, string reason);

    /// <summary>
    /// Truncate provider exception/error strings before surfacing them to agent-visible
    /// content. Deep stack-or-internal-detail strings would otherwise leak into the LLM's
    /// reasoning context (and into chat UIs).
    /// </summary>
    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}
