using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Messages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.WebChat;

/// <summary>
/// Browser-facing SignalR hub served by the Bridge on <c>127.0.0.1:5080</c>.
/// Browser clients connect here to send/receive chat messages.
/// The hub delegates inbound messages to <see cref="WebChatChannel"/> and
/// pushes outbound events (streaming chunks, final responses) back to the browser.
/// History is now loaded from the Bridge REST API (GET /api/messages/webchat-default).
/// </summary>
public sealed partial class WebChatHub : Hub
{
    private readonly WebChatChannel channel;
    private readonly IWebChatHubProxy hubProxy;
    private readonly ILogger<WebChatHub> logger;
    private readonly IHostApplicationLifetime lifetime;

    public WebChatHub(
        WebChatChannel channel,
        IWebChatHubProxy hubProxy,
        ILogger<WebChatHub> logger,
        IHostApplicationLifetime lifetime)
    {
        this.channel = channel;
        this.hubProxy = hubProxy;
        this.logger = logger;
        this.lifetime = lifetime;
    }

    /// <summary>
    /// Browser sends a chat message.
    /// </summary>
    public async Task SendMessage(string conversationId, string text)
    {
        var connectionId = Context.ConnectionId;
        this.LogMessageReceived(connectionId, conversationId);

        if (string.IsNullOrWhiteSpace(text))
        {
            await Clients.Caller.SendAsync("OnError", conversationId, "Message text cannot be empty.").ConfigureAwait(false);
            return;
        }

        var message = new InboundMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            ChannelId = this.channel.ChannelId,
            ChannelType = ChannelType.WebChat,
            Sender = new SenderInfo
            {
                Id = connectionId,
                DisplayName = "Web User",
            },
            Content = new MessageContent { Text = text },
            Timestamp = DateTimeOffset.UtcNow,
        };

        await this.channel.ReceiveFromBrowserAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Browser requests agent status.
    /// </summary>
    public async Task<AgentStatusInfo> GetStatus()
    {
        return await this.hubProxy.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Browser requests to abort an in-progress generation.
    /// </summary>
    public async Task AbortGeneration(string conversationId)
    {
        this.LogAbortRequested(conversationId);
        await this.hubProxy.AbortGenerationAsync(conversationId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        // Refuse connections that arrive while the host is draining. A WebSocket is a
        // long-lived in-flight request, so one accepted here would hold Kestrel's
        // graceful shutdown open until the host shutdown timeout elapses and the
        // process is force-killed. The Web UI's SignalR client reconnects the instant
        // shutdown drops its socket, which lands squarely in that window on restart.
        if (this.lifetime.ApplicationStopping.IsCancellationRequested)
        {
            this.LogConnectionRefusedDuringShutdown(Context.ConnectionId);
            Context.Abort();
            return;
        }

        this.LogClientConnected(Context.ConnectionId);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        this.LogClientDisconnected(Context.ConnectionId, exception?.Message);
        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    // ── LoggerMessage source-generated methods ───────────────────────

    [LoggerMessage(Level = LogLevel.Debug, Message = "WebChat message received from {ConnectionId} for conversation {ConversationId}")]
    private partial void LogMessageReceived(string connectionId, string conversationId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat client connected: {ConnectionId}")]
    private partial void LogClientConnected(string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat connection {ConnectionId} refused: the Bridge is shutting down")]
    private partial void LogConnectionRefusedDuringShutdown(string connectionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "WebChat client disconnected: {ConnectionId}, reason={Reason}")]
    private partial void LogClientDisconnected(string connectionId, string? reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Abort generation requested for conversation {ConversationId}")]
    private partial void LogAbortRequested(string conversationId);
}
