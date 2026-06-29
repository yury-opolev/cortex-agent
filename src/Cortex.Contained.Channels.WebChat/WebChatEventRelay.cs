using Cortex.Contained.Contracts.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.WebChat;

/// <summary>
/// Background service that subscribes to <see cref="WebChatChannel"/> outbound
/// events and pushes them to all connected browser clients through
/// <see cref="IHubContext{WebChatHub}"/>.
/// </summary>
public sealed partial class WebChatEventRelay : IHostedService
{
    private readonly WebChatChannel channel;
    private readonly IHubContext<WebChatHub> hubContext;
    private readonly ILogger<WebChatEventRelay> logger;

    public WebChatEventRelay(
        WebChatChannel channel,
        IHubContext<WebChatHub> hubContext,
        ILogger<WebChatEventRelay> logger)
    {
        this.channel = channel;
        this.hubContext = hubContext;
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        this.channel.OutboundMessageReady += OnOutboundMessageAsync;
        this.channel.TypingIndicatorReady += OnTypingIndicatorAsync;
        this.channel.StreamingUpdateReady += OnStreamingUpdateAsync;
        this.channel.StreamingFinalizeReady += OnStreamingFinalizeAsync;

        this.LogRelayStarted();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        this.channel.OutboundMessageReady -= OnOutboundMessageAsync;
        this.channel.TypingIndicatorReady -= OnTypingIndicatorAsync;
        this.channel.StreamingUpdateReady -= OnStreamingUpdateAsync;
        this.channel.StreamingFinalizeReady -= OnStreamingFinalizeAsync;

        this.LogRelayStopped();
        return Task.CompletedTask;
    }

    private async Task OnOutboundMessageAsync(OutboundMessage message)
    {
        this.LogOutboundMessage(message.ConversationId, message.MessageId);

        await this.hubContext.Clients.All.SendAsync(
            "OnMessage",
            message.ConversationId,
            message.MessageId,
            message.Content.Text ?? string.Empty).ConfigureAwait(false);
    }

    private async Task OnTypingIndicatorAsync(string conversationId)
    {
        await this.hubContext.Clients.All.SendAsync(
            "OnTyping",
            conversationId).ConfigureAwait(false);
    }

    private async Task OnStreamingUpdateAsync(string conversationId, string partialText)
    {
        this.LogStreamingUpdate(conversationId);

        await this.hubContext.Clients.All.SendAsync(
            "OnStreamingUpdate",
            conversationId,
            partialText).ConfigureAwait(false);
    }

    private async Task OnStreamingFinalizeAsync(string conversationId, OutboundMessage finalMessage)
    {
        this.LogStreamingFinalize(conversationId, finalMessage.MessageId);

        await this.hubContext.Clients.All.SendAsync(
            "OnStreamingFinalize",
            conversationId,
            finalMessage.MessageId,
            finalMessage.Content.Text ?? string.Empty,
            finalMessage.IsThinking).ConfigureAwait(false);
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat event relay started")]
    private partial void LogRelayStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat event relay stopped")]
    private partial void LogRelayStopped();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outbound message for conversation {ConversationId}: {MessageId}")]
    private partial void LogOutboundMessage(string conversationId, string messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Streaming update for conversation {ConversationId}")]
    private partial void LogStreamingUpdate(string conversationId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Streaming finalized for conversation {ConversationId}: {MessageId}")]
    private partial void LogStreamingFinalize(string conversationId, string messageId);
}
