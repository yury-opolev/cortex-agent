using System.Text.Json;
using Cortex.Contained.Channels.CloudMessaging.Envelope;
using Cortex.Contained.Channels.CloudMessaging.Mapping;
using Cortex.Contained.Channels.CloudMessaging.Negotiate;
using Cortex.Contained.Channels.CloudMessaging.Reconnect;
using Cortex.Contained.Channels.CloudMessaging.Security;
using Cortex.Contained.Channels.CloudMessaging.Transport;
using Cortex.Contained.Channels.WebChat;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.CloudMessaging;

/// <summary>
/// Outbound <see cref="IChannel"/> that connects the home Bridge to the AI Messenger
/// cloud service (Azure Web PubSub). Authenticates via a bridge credential, calls
/// <c>POST /negotiate-bridge</c> to obtain a group-scoped Web PubSub URL, and then:
/// <list type="bullet">
///   <item>Inbound: user "text" envelopes → <see cref="WebChatChannel.ReceiveFromBrowserAsync"/>.</item>
///   <item>Outbound: <see cref="WebChatChannel"/> events → cloud envelope frames sent to the tenant group.</item>
/// </list>
/// Config-gated; off by default (the <c>channels:cloud-messaging:enabled</c> flag must be true).
/// Reconnects with exponential backoff on any transport failure, mirroring the Discord channel pattern.
/// </summary>
public sealed partial class CloudMessagingChannel : IChannelWithStreaming
{
    private readonly ILogger<CloudMessagingChannel> logger;
    private readonly CloudMessagingChannelOptions options;
    private readonly ICloudNegotiateClient negotiateClient;
    private readonly ICloudTransport transport;
    private readonly WebChatChannel webChatChannel;

    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private ChannelStatus status = ChannelStatus.Disconnected;
    private CancellationTokenSource? connectLoopCts;
    private Task? connectLoopTask;

    // Set after a successful negotiate; validated against every inbound frame.
    private IReadOnlyList<string> allowedTenants = [];

    public CloudMessagingChannel(
        ILogger<CloudMessagingChannel> logger,
        CloudMessagingChannelOptions options,
        ICloudNegotiateClient negotiateClient,
        ICloudTransport transport,
        WebChatChannel webChatChannel)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.negotiateClient = negotiateClient ?? throw new ArgumentNullException(nameof(negotiateClient));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.webChatChannel = webChatChannel ?? throw new ArgumentNullException(nameof(webChatChannel));

        // Wire outbound events from WebChatChannel → cloud service
        this.webChatChannel.OutboundMessageReady += this.OnOutboundMessageReadyAsync;
        this.webChatChannel.StreamingUpdateReady += this.OnStreamingUpdateReadyAsync;
        this.webChatChannel.StreamingFinalizeReady += this.OnStreamingFinalizeReadyAsync;
        this.webChatChannel.TypingIndicatorReady += this.OnTypingIndicatorReadyAsync;
    }

    // ── IChannel ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public string ChannelId => this.options.ChannelId;

    /// <inheritdoc />
    public ChannelType Type => ChannelType.CloudMessaging;

    /// <inheritdoc />
    public ChannelStatus Status => this.status;

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; } = new()
    {
        SupportsStreaming = true,
        SupportsRichText = true,
        SupportsEditing = false,
        SupportsDeletion = false,
        SupportsMedia = false,
        MaxMessageLength = 100_000,
    };

    /// <inheritdoc />
    public event Func<InboundMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Func<ChannelStatusChange, Task>? StatusChanged;

    // ── IChannel methods ──────────────────────────────────────────────

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (this.status == ChannelStatus.Connected || this.status == ChannelStatus.Connecting)
        {
            return Task.CompletedTask;
        }

        this.SetStatus(ChannelStatus.Connecting, "Starting cloud-messaging connect loop");
        this.LogConnecting(this.ChannelId, this.options.ServiceBaseUrl);

        this.connectLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        this.connectLoopTask = Task.Run(
            () => this.RunConnectLoopAsync(this.connectLoopCts.Token),
            this.connectLoopCts.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        this.LogDisconnecting(this.ChannelId);

        if (this.connectLoopCts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (this.connectLoopTask is { } loopTask)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — we cancelled it
            }
        }

        await this.transport.CloseAsync(ct).ConfigureAwait(false);
        this.SetStatus(ChannelStatus.Disconnected, "Channel disconnected");
        this.LogDisconnected(this.ChannelId);
    }

    /// <inheritdoc />
    public async Task<SendResult> SendMessageAsync(OutboundMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Non-streaming full message: find a tenant for this conversation
        var tenantId = this.ResolveTenantForConversation(message.ConversationId);
        if (tenantId is null)
        {
            this.LogNoTenantForConversation(this.ChannelId, message.ConversationId);
            return SendResult.Error("No tenant resolved for conversation");
        }

        var envelope = CloudEnvelopeMapper.ToTextEnvelope(message, tenantId);
        return await this.SendEnvelopeAsync(envelope, ct).ConfigureAwait(false);
    }

    // ── IChannelWithStreaming ─────────────────────────────────────────

    /// <inheritdoc />
    public async Task SendTypingIndicatorAsync(string conversationId, CancellationToken ct = default)
    {
        var tenantId = this.ResolveTenantForConversation(conversationId);
        if (tenantId is null)
        {
            return;
        }

        var envelope = CloudEnvelopeMapper.ToTypingEnvelope(conversationId, tenantId);
        _ = await this.SendEnvelopeAsync(envelope, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendStreamingUpdateAsync(string conversationId, string partialText, CancellationToken ct = default)
    {
        var tenantId = this.ResolveTenantForConversation(conversationId);
        if (tenantId is null)
        {
            return;
        }

        var messageId = Guid.NewGuid().ToString("N");
        var envelope = CloudEnvelopeMapper.ToStreamChunkEnvelope(conversationId, partialText, tenantId, messageId);
        _ = await this.SendEnvelopeAsync(envelope, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FinalizeStreamingAsync(string conversationId, OutboundMessage finalMessage, CancellationToken ct = default)
    {
        var tenantId = this.ResolveTenantForConversation(conversationId);
        if (tenantId is null)
        {
            return;
        }

        var envelope = CloudEnvelopeMapper.ToFinalizeEnvelope(conversationId, finalMessage, tenantId);
        _ = await this.SendEnvelopeAsync(envelope, ct).ConfigureAwait(false);
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        this.webChatChannel.OutboundMessageReady -= this.OnOutboundMessageReadyAsync;
        this.webChatChannel.StreamingUpdateReady -= this.OnStreamingUpdateReadyAsync;
        this.webChatChannel.StreamingFinalizeReady -= this.OnStreamingFinalizeReadyAsync;
        this.webChatChannel.TypingIndicatorReady -= this.OnTypingIndicatorReadyAsync;

        await this.DisconnectAsync().ConfigureAwait(false);
        await this.transport.DisposeAsync().ConfigureAwait(false);
    }

    // ── Connect loop (reconnect-with-backoff) ─────────────────────────

    private async Task RunConnectLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        var random = new Random();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                this.LogNegotiating(this.ChannelId, attempt);
                this.SetStatus(ChannelStatus.Connecting, $"Negotiating (attempt {attempt})");

                var negotiateResult = await this.negotiateClient
                    .NegotiateAsync(ct)
                    .ConfigureAwait(false);

                this.allowedTenants = negotiateResult.Tenants;
                this.LogNegotiated(this.ChannelId, negotiateResult.Tenants.Count);

                await this.transport.ConnectAsync(negotiateResult.Url, ct).ConfigureAwait(false);

                this.SetStatus(ChannelStatus.Connected, "Connected to cloud service");
                this.LogConnected(this.ChannelId);
                attempt = 0; // Reset backoff on successful connect

                // Receive loop — runs until transport closes or ct is cancelled
                await this.RunReceiveLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Clean shutdown — exit loop
                break;
            }
#pragma warning disable CA1031 // Broad catch: reconnect on any transport / negotiate failure
            catch (Exception ex)
            {
                this.LogConnectFailed(this.ChannelId, attempt, ex);
            }
#pragma warning restore CA1031

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Exponential backoff before next attempt
            this.SetStatus(ChannelStatus.Reconnecting, $"Waiting to retry (attempt {attempt})");
            var delay = BackoffDecision.ComputeDelay(
                attempt,
                this.options.ReconnectBaseDelayMs,
                this.options.ReconnectMaxDelayMs,
                jitterMs: 2_000,
                randomJitter: random.NextDouble());

            this.LogReconnectDelay(this.ChannelId, (int)delay.TotalMilliseconds);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            attempt++;
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && this.transport.IsConnected)
        {
            string? json;

            try
            {
                json = await this.transport.ReceiveTextAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            if (json is null)
            {
                // Connection closed by server
                this.LogTransportClosed(this.ChannelId);
                return;
            }

            // Size guard — drop oversized frames (fail closed)
            if (json.Length > this.options.MaxFrameBytes)
            {
                this.LogFrameOversized(this.ChannelId, json.Length, this.options.MaxFrameBytes);
                continue;
            }

            await this.DispatchInboundFrameAsync(json, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchInboundFrameAsync(string json, CancellationToken ct)
    {
        CloudEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<CloudEnvelope>(json, this.jsonOptions);
        }
        catch (JsonException ex)
        {
            this.LogFrameMalformed(this.ChannelId, ex.Message);
            return;
        }

        if (envelope is null)
        {
            this.LogFrameMalformed(this.ChannelId, "deserialized to null");
            return;
        }

        // Tenant-allow guard — reject frames from tenants this bridge does not serve
        if (!TenantAllowDecision.IsAllowed(envelope.TenantId, this.allowedTenants))
        {
            this.LogTenantRejected(this.ChannelId, envelope.TenantId ?? "(null)");
            return;
        }

        // Only "text" frames come inbound from users; route all others to the WebChatChannel
        if (!string.Equals(envelope.Type, EnvelopeTypes.Text, StringComparison.Ordinal))
        {
            // Silently ignore non-text inbound frames (typing indicators, etc. from other clients)
            return;
        }

        var inbound = CloudEnvelopeMapper.ToInboundMessage(envelope, this.ChannelId);
        if (inbound is null)
        {
            this.LogFrameMalformed(this.ChannelId, "failed to map to InboundMessage");
            return;
        }

        this.LogInboundReceived(this.ChannelId, envelope.Id, envelope.TenantId);

        // Track the tenant for this conversation so outbound routing can resolve it
        this.TrackConversationTenant(inbound.ConversationId, envelope.TenantId);

        // Route to the WebChatChannel seam
        await this.webChatChannel.ReceiveFromBrowserAsync(inbound).ConfigureAwait(false);

        // Also raise MessageReceived for any other subscribers
        if (MessageReceived is { } handler)
        {
            await handler(inbound).ConfigureAwait(false);
        }
    }

    // ── Outbound event handlers from WebChatChannel ───────────────────

    private async Task OnOutboundMessageReadyAsync(OutboundMessage message)
    {
        _ = await this.SendMessageAsync(message).ConfigureAwait(false);
    }

    private async Task OnStreamingUpdateReadyAsync(string conversationId, string partialText)
    {
        await this.SendStreamingUpdateAsync(conversationId, partialText).ConfigureAwait(false);
    }

    private async Task OnStreamingFinalizeReadyAsync(string conversationId, OutboundMessage finalMessage)
    {
        await this.FinalizeStreamingAsync(conversationId, finalMessage).ConfigureAwait(false);
    }

    private async Task OnTypingIndicatorReadyAsync(string conversationId)
    {
        await this.SendTypingIndicatorAsync(conversationId).ConfigureAwait(false);
    }

    // ── Tenant tracking (conversation → tenant) ───────────────────────

    // Lightweight in-memory map: conversationId → tenantId.
    // Not persisted — repopulated from inbound frames after reconnect.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> conversationTenantMap = new();

    private void TrackConversationTenant(string conversationId, string tenantId)
        => this.conversationTenantMap[conversationId] = tenantId;

    private string? ResolveTenantForConversation(string conversationId)
    {
        if (this.conversationTenantMap.TryGetValue(conversationId, out var tenantId))
        {
            return tenantId;
        }

        // Fallback: if there is exactly one allowed tenant, use it
        if (this.allowedTenants.Count == 1)
        {
            return this.allowedTenants[0];
        }

        return null;
    }

    // ── Send helper ───────────────────────────────────────────────────

    private async Task<SendResult> SendEnvelopeAsync(CloudEnvelope envelope, CancellationToken ct = default)
    {
        if (!this.transport.IsConnected)
        {
            this.LogSendSkippedNotConnected(this.ChannelId, envelope.Type);
            return SendResult.Error("Transport not connected");
        }

        try
        {
            var json = JsonSerializer.Serialize(envelope, this.jsonOptions);
            await this.transport.SendTextAsync(json, ct).ConfigureAwait(false);
            this.LogEnvelopeSent(this.ChannelId, envelope.Type, envelope.ConversationId);
            return SendResult.Ok(envelope.Id);
        }
        catch (OperationCanceledException)
        {
            return SendResult.Error("Cancelled");
        }
#pragma warning disable CA1031 // Broad catch: sending must not crash the caller
        catch (Exception ex)
        {
            this.LogSendFailed(this.ChannelId, envelope.Type, ex);
            return SendResult.Error(ex.Message);
        }
#pragma warning restore CA1031
    }

    // ── Status helper ─────────────────────────────────────────────────

    private void SetStatus(ChannelStatus newStatus, string? reason = null)
    {
        var previous = this.status;
        this.status = newStatus;

        StatusChanged?.Invoke(new ChannelStatusChange(previous, newStatus, reason));
    }

    // ── Source-generated LoggerMessage ────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} connecting to {ServiceBaseUrl}")]
    private partial void LogConnecting(string channelId, string serviceBaseUrl);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} connected to cloud service")]
    private partial void LogConnected(string channelId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} disconnecting")]
    private partial void LogDisconnecting(string channelId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} disconnected")]
    private partial void LogDisconnected(string channelId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} negotiating (attempt {Attempt})")]
    private partial void LogNegotiating(string channelId, int attempt);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} negotiated; serving {TenantCount} tenant(s)")]
    private partial void LogNegotiated(string channelId, int tenantCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[cloud-msg] Channel {ChannelId} connect failed (attempt {Attempt})")]
    private partial void LogConnectFailed(string channelId, int attempt, Exception error);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} waiting {DelayMs}ms before next reconnect")]
    private partial void LogReconnectDelay(string channelId, int delayMs);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "[cloud-msg] Channel {ChannelId} transport closed by server")]
    private partial void LogTransportClosed(string channelId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[cloud-msg] Channel {ChannelId} dropped oversized frame ({FrameBytes} > {MaxBytes} bytes)")]
    private partial void LogFrameOversized(string channelId, int frameBytes, int maxBytes);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[cloud-msg] Channel {ChannelId} dropped malformed frame: {Reason}")]
    private partial void LogFrameMalformed(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[cloud-msg] Channel {ChannelId} rejected inbound frame for unserved tenant '{TenantId}'")]
    private partial void LogTenantRejected(string channelId, string tenantId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[cloud-msg] Channel {ChannelId} received inbound frame {FrameId} for tenant '{TenantId}'")]
    private partial void LogInboundReceived(string channelId, string frameId, string tenantId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[cloud-msg] Channel {ChannelId} sent {EnvelopeType} envelope for conversation {ConversationId}")]
    private partial void LogEnvelopeSent(string channelId, string envelopeType, string conversationId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[cloud-msg] Channel {ChannelId} skipped sending {EnvelopeType}: transport not connected")]
    private partial void LogSendSkippedNotConnected(string channelId, string envelopeType);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "[cloud-msg] Channel {ChannelId} failed to send {EnvelopeType} envelope")]
    private partial void LogSendFailed(string channelId, string envelopeType, Exception error);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "[cloud-msg] Channel {ChannelId} no tenant resolved for conversation {ConversationId}; dropping outbound message")]
    private partial void LogNoTenantForConversation(string channelId, string conversationId);
}
