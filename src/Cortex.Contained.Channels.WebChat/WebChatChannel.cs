using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.WebChat;

/// <summary>
/// IChannel adapter for browser-based web chat. Does not connect to an external service;
/// instead, <see cref="WebChatHub"/> subscribes to the outbound events and pushes inbound
/// messages through <see cref="ReceiveFromBrowserAsync"/>.
/// </summary>
public sealed partial class WebChatChannel : IChannelWithStreaming
{
    private readonly ILogger<WebChatChannel> logger;
    private ChannelStatus status = ChannelStatus.Disconnected;

    public WebChatChannel(ILogger<WebChatChannel> logger, string channelId = "webchat-default")
    {
        this.logger = logger;
        ChannelId = channelId;
    }

    // ── IChannel properties ──────────────────────────────────────────

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <inheritdoc />
    public ChannelType Type => ChannelType.WebChat;

    /// <inheritdoc />
    public ChannelStatus Status => this.status;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; } = new()
    {
        SupportsStreaming = true,
        SupportsRichText = true,
        SupportsEditing = false,
        SupportsDeletion = false,
        SupportsMedia = false, // Phase 3 MVP
        MaxMessageLength = 100_000,
    };

    // ── IChannel events ──────────────────────────────────────────────

    /// <inheritdoc />
    public event Func<InboundMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Func<ChannelStatusChange, Task>? StatusChanged;

    // ── Events for WebChatHub ────────────────────────────────────────

    /// <summary>Raised when the bridge sends an outbound message.</summary>
    public event Func<OutboundMessage, Task>? OutboundMessageReady;

    /// <summary>Raised when the bridge sends a typing indicator.</summary>
    public event Func<string, Task>? TypingIndicatorReady;

    /// <summary>Raised when the bridge sends a streaming text update.</summary>
    public event Func<string, string, Task>? StreamingUpdateReady;

    /// <summary>Raised when the bridge finalizes a streaming message.</summary>
    public event Func<string, OutboundMessage, Task>? StreamingFinalizeReady;

    // ── IChannel methods ─────────────────────────────────────────────

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        SetStatus(ChannelStatus.Connected, "Channel connected");
        this.LogConnected(ChannelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        SetStatus(ChannelStatus.Disconnected, "Channel disconnected");
        this.LogDisconnected(ChannelId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SendResult> SendMessageAsync(OutboundMessage message, CancellationToken ct = default)
    {
        this.LogOutboundMessageReady(ChannelId, message.MessageId);

        if (OutboundMessageReady is { } handler)
        {
            await handler(message).ConfigureAwait(false);
        }

        return SendResult.Ok(message.MessageId);
    }

    // ── IChannelWithStreaming methods ─────────────────────────────────

    /// <inheritdoc />
    public async Task SendTypingIndicatorAsync(string conversationId, CancellationToken ct = default)
    {
        if (TypingIndicatorReady is { } handler)
        {
            await handler(conversationId).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SendStreamingUpdateAsync(string conversationId, string partialText, CancellationToken ct = default)
    {
        this.LogStreamingUpdate(ChannelId, conversationId);

        if (StreamingUpdateReady is { } handler)
        {
            await handler(conversationId, partialText).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task FinalizeStreamingAsync(string conversationId, OutboundMessage finalMessage, CancellationToken ct = default)
    {
        if (StreamingFinalizeReady is { } handler)
        {
            await handler(conversationId, finalMessage).ConfigureAwait(false);
        }
    }

    // ── Inbound from browser ─────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="WebChatHub"/> when a browser user sends a message.
    /// </summary>
    public async Task ReceiveFromBrowserAsync(InboundMessage message)
    {
        this.LogMessageReceivedFromBrowser(ChannelId, message.MessageId);

        if (MessageReceived is { } handler)
        {
            await handler(message).ConfigureAwait(false);
        }
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this.status != ChannelStatus.Disconnected)
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void SetStatus(ChannelStatus newStatus, string? reason = null)
    {
        var previous = this.status;
        this.status = newStatus;

        StatusChanged?.Invoke(new ChannelStatusChange(previous, newStatus, reason));
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat channel {ChannelId} connected")]
    private partial void LogConnected(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat channel {ChannelId} disconnected")]
    private partial void LogDisconnected(string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebChat channel {ChannelId} received message {MessageId} from browser")]
    private partial void LogMessageReceivedFromBrowser(string channelId, string messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebChat channel {ChannelId} outbound message {MessageId} ready")]
    private partial void LogOutboundMessageReady(string channelId, string messageId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebChat channel {ChannelId} streaming update for conversation {ConversationId}")]
    private partial void LogStreamingUpdate(string channelId, string conversationId);
}
