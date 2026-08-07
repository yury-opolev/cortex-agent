using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Bridge.Connectors.Replay;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Protocol state machine for a single connector WebSocket connection.
/// Handles the full lifecycle: handshake, read loop, and teardown.
/// </summary>
public sealed partial class ConnectorSession : IAsyncDisposable
{
    /// <summary>Interval in seconds between ping frames sent to the connector.</summary>
    internal const int PingIntervalSeconds = 30;

    /// <summary>Seconds after which a connector is considered dead if no frame has been received.</summary>
    internal const int HeartbeatTimeoutSeconds = 90;

    /// <summary>
    /// Upper bound on the conversation ids a single session may claim ownership of, so a
    /// connector cannot grow the set without limit by cycling ids.
    /// </summary>
    internal const int MaxTrackedConversations = 256;

    /// <summary>
    /// Seconds within which the connector must send its <c>hello</c> frame after connecting.
    /// A connector that holds a slot without ever handshaking is treated as non-functional.
    /// </summary>
    internal const int HandshakeTimeoutSeconds = 10;

    /// <summary>Maximum length of id fields (messageId, conversationId, sender.id).</summary>
    /// <remarks>Reject on excess — ids are identity and truncating them silently would corrupt routing.</remarks>
    internal const int MaxIdLength = 128;

    /// <summary>Maximum length of display-name fields (sender.displayName, hello.displayName).</summary>
    /// <remarks>Truncate on excess — display names are cosmetic and safe to shorten.</remarks>
    internal const int MaxDisplayNameLength = 256;

    private static readonly TimeSpan RateLimitLogSuppression = TimeSpan.FromMinutes(1);

    private readonly IConnectorTransport transport;
    private readonly IConnectorAuthenticator authenticator;
    private readonly ConnectorSettingsConfig settings;
    private readonly IConnectorRegistry registry;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<ConnectorSession> logger;
    private readonly TimeProvider timeProvider;
    private readonly IConnectorAbortDispatcher abortDispatcher;
    private readonly IConnectorReplaySource replaySource;
    private readonly ConnectorRateLimiter rateLimiter;
    private readonly Lock teardownLock = new();
    private readonly Lock conversationLock = new();
    private readonly HashSet<string> ownedConversations = new(StringComparer.Ordinal);
    private bool conversationCapLogged;
    private ITimer? pingTimer;
    private bool tornDown;
    private DateTimeOffset? lastRateLimitLoggedAt;

    /// <summary>The plugin channel created during the handshake phase.</summary>
    public PluginChannel? Channel { get; private set; }

    /// <summary>The channel id assigned during the handshake phase.</summary>
    public string? ChannelId => this.Channel?.ChannelId;

    /// <summary>Timestamp of the last frame received from the connector.</summary>
    public DateTimeOffset? LastSeenUtc { get; private set; }

    /// <summary>
    /// Initialises a new <see cref="ConnectorSession"/>.
    /// </summary>
    public ConnectorSession(
        IConnectorTransport transport,
        IConnectorAuthenticator authenticator,
        ConnectorSettingsConfig settings,
        IConnectorRegistry registry,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IConnectorAbortDispatcher abortDispatcher,
        IConnectorReplaySource replaySource)
    {
        this.transport = transport;
        this.authenticator = authenticator;
        this.settings = settings;
        this.registry = registry;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<ConnectorSession>();
        this.timeProvider = timeProvider;
        this.abortDispatcher = abortDispatcher;
        this.replaySource = replaySource;
        this.rateLimiter = new ConnectorRateLimiter(settings.Limits.MaxMessagesPerMinute, timeProvider);
    }

    /// <summary>
    /// Runs the full session lifetime: handshake, read loop, teardown.
    /// Never throws for protocol or peer errors; only
    /// <see cref="OperationCanceledException"/> from <paramref name="ct"/> may propagate.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await this.RunInternalAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogUnexpectedSessionError(ex);
        }
        finally
        {
            await this.TeardownAsync().ConfigureAwait(false);
        }
    }

    private async Task RunInternalAsync(CancellationToken ct)
    {
        string? firstJson;

        using var handshakeCts = new CancellationTokenSource();
        // CreateCancellationTokenSource is not available in this TFM; use a timer so
        // FakeTimeProvider can advance past the deadline in tests without real wall-clock delay.
        using var handshakeTimer = this.timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            handshakeCts,
            TimeSpan.FromSeconds(HandshakeTimeoutSeconds),
            Timeout.InfiniteTimeSpan);
        using var linkedHandshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeCts.Token);

        try
        {
            firstJson = await this.transport.ReceiveAsync(linkedHandshakeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (handshakeCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await this.SendErrorAndCloseAsync(ConnectorErrorCodes.ProtocolViolation, "handshake_timeout", ct).ConfigureAwait(false);
            return;
        }
        catch (ConnectorFrameTooLargeException ex)
        {
            await this.SendErrorAndCloseAsync(ConnectorErrorCodes.FrameTooLarge, ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (InvalidOperationException ex)
        {
            await this.SendErrorAndCloseAsync(ConnectorErrorCodes.MalformedFrame, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        if (firstJson is null)
        {
            this.LogPeerClosedBeforeHello(this.transport.RemoteEndpoint);
            return;
        }

        if (!ConnectorFrameParser.TryParse(firstJson, out var frame, out var parseErrorCode, out var parseErrorMessage))
        {
            await this.SendErrorAndCloseAsync(parseErrorCode!, parseErrorMessage!, ct).ConfigureAwait(false);
            return;
        }

        if (frame!.Type != ConnectorFrameTypes.Hello)
        {
            await this.SendErrorAndCloseAsync(
                ConnectorErrorCodes.ProtocolViolation,
                $"Expected hello frame, got '{frame.Type}'.",
                ct).ConfigureAwait(false);
            return;
        }

        if (!ConnectorFrameParser.TryDeserializePayload<ConnectorHelloPayload>(frame, out var hello, out var helloError))
        {
            await this.SendErrorAndCloseAsync(ConnectorErrorCodes.InvalidPayload, helloError!, ct).ConfigureAwait(false);
            return;
        }

        var normalizedKey = ConnectorChannelId.Normalize(hello!.Key);
        if (normalizedKey is null)
        {
            await this.SendErrorAndCloseAsync(
                ConnectorErrorCodes.InvalidPayload,
                "hello.key is missing or invalid.",
                ct).ConfigureAwait(false);
            return;
        }

        var rawInstanceId = string.IsNullOrWhiteSpace(hello.InstanceId) ? "default" : hello.InstanceId;
        var normalizedInstanceId = ConnectorChannelId.Normalize(rawInstanceId);
        if (normalizedInstanceId is null)
        {
            await this.SendErrorAndCloseAsync(
                ConnectorErrorCodes.InvalidPayload,
                "hello.instanceId is invalid.",
                ct).ConfigureAwait(false);
            return;
        }

        var channelId = ConnectorChannelId.Create(normalizedKey, normalizedInstanceId);

        // Truncate display name — it is cosmetic, not identity, so truncation is safe.
        var rawDisplayName = string.IsNullOrWhiteSpace(hello.DisplayName) ? normalizedKey : hello.DisplayName;
        var displayName = ConnectorText.Truncate(rawDisplayName, MaxDisplayNameLength)!;

        ConnectorAuthResult authResult;
        try
        {
            authResult = await this.authenticator.AuthenticateAsync(
                new ConnectorAuthRequest
                {
                    Key = normalizedKey,
                    InstanceId = normalizedInstanceId,
                    DisplayName = displayName,
                    Token = hello.Token,
                    RemoteEndpoint = this.transport.RemoteEndpoint,
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogAuthenticatorError(ex);
            await this.SendErrorAndCloseAsync(ConnectorErrorCodes.ProtocolViolation, "Authentication error.", ct).ConfigureAwait(false);
            return;
        }

        authResult = await this.ResolveAuthResultAsync(authResult, ct).ConfigureAwait(false);

        if (authResult.Outcome == ConnectorAuthOutcome.Denied)
        {
            await this.SendFrameAsync(ConnectorFrameTypes.PairingDenied, new ConnectorPairingDeniedPayload
            {
                Reason = authResult.Reason ?? "Access denied.",
            }, ct).ConfigureAwait(false);
            await this.transport.CloseAsync("pairing denied", ct).ConfigureAwait(false);
            return;
        }

        if (authResult.IssuedToken is not null)
        {
            await this.SendFrameAsync(ConnectorFrameTypes.Paired, new ConnectorPairedPayload
            {
                Token = authResult.IssuedToken,
                ChannelId = channelId,
            }, ct).ConfigureAwait(false);
        }

        var caps = hello.Capabilities;
        var capabilities = new ChannelCapabilities
        {
            SupportsStreaming = caps?.Streaming ?? false,
            SupportsRichText = caps?.RichText ?? false,
            SupportsMedia = false,
            MaxMessageLength = Math.Clamp(caps?.MaxMessageLength ?? 100_000, 1, 100_000),
        };

        var pluginChannel = PluginChannelFactory.Create(
            normalizedKey,
            normalizedInstanceId,
            capabilities,
            displayName,
            this.loggerFactory);
        pluginChannel.OutboundSink = this.OutboundSinkAsync;
        pluginChannel.FrameSink = this.FrameSinkAsync;

        var attachResult = await this.registry.TryAttachAsync(pluginChannel, ct).ConfigureAwait(false);
        if (!attachResult.Success)
        {
            await this.SendErrorAndCloseAsync(
                attachResult.ErrorCode ?? ConnectorErrorCodes.ProtocolViolation,
                attachResult.ErrorMessage ?? "Failed to attach channel.",
                ct).ConfigureAwait(false);
            return;
        }

        this.Channel = pluginChannel;
        this.LogSessionReady(channelId, this.transport.RemoteEndpoint);

        // Determine what to replay. A connector that omits sinceCursor gets no replay (by design).
        // An UNPARSEABLE cursor also gets no replay rather than being treated as epoch, which would
        // flood the connector with the entire history — fail closed, not open.
        IReadOnlyList<ConnectorReplayMessage> replayMessages = [];
        if (!ConnectorCursor.TryParse(hello.SinceCursor, out var since))
        {
            this.LogNoCursor(channelId, hello.SinceCursor is null ? "absent" : "unparseable");
        }
        else
        {
            try
            {
                replayMessages = await this.replaySource
                    .GetMissedMessagesAsync(channelId, since, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogReplaySourceFailed(channelId, ex);
                replayMessages = [];
            }
        }

        // Send ready FIRST with the replay count so the connector knows how many frames to expect
        // before entering steady state; the outbound replay frames follow immediately after.
        await this.SendFrameAsync(ConnectorFrameTypes.Ready, new ConnectorReadyPayload
        {
            ChannelId = channelId,
            ReplayCount = replayMessages.Count,
        }, ct).ConfigureAwait(false);

        // Send replay frames. A failure on any single frame aborts replay but NOT the session.
        foreach (var replayMsg in replayMessages)
        {
            if (!this.transport.IsOpen)
            {
                break;
            }

            try
            {
                var replayPayload = new ConnectorOutboundPayload
                {
                    MessageId = replayMsg.MessageId,
                    ConversationId = replayMsg.ConversationId,
                    Content = new ConnectorContentPayload
                    {
                        Text = replayMsg.Text,
                        IsMarkdown = false,
                    },
                    IsThinking = false,
                    Cursor = ConnectorCursor.Format(replayMsg.Timestamp),
                };
                var replayJson = ConnectorFrame.Serialize(ConnectorFrameTypes.Outbound, replayPayload);
                await this.transport.SendAsync(replayJson, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogReplayFrameFailed(channelId, replayMsg.MessageId, ex);
                break;
            }
        }

        this.LastSeenUtc = this.timeProvider.GetUtcNow();
        this.pingTimer = this.timeProvider.CreateTimer(
            this.OnPingTimerTickAsync,
            null,
            TimeSpan.FromSeconds(PingIntervalSeconds),
            TimeSpan.FromSeconds(PingIntervalSeconds));

        await this.ReadLoopAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            string? json;
            try
            {
                json = await this.transport.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (ConnectorFrameTooLargeException ex)
            {
                await this.SendErrorAndCloseAsync(ConnectorErrorCodes.FrameTooLarge, ex.Message, ct).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex)
            {
                await this.SendErrorAndCloseAsync(ConnectorErrorCodes.MalformedFrame, ex.Message, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                this.LogReadLoopFault(ex);
                return;
            }

            if (json is null)
            {
                break;
            }

            if (!ConnectorFrameParser.TryParse(json, out var frame, out var errorCode, out var errorMessage))
            {
                await this.SendErrorAndCloseAsync(errorCode!, errorMessage!, ct).ConfigureAwait(false);
                return;
            }

            await this.HandleFrameAsync(frame!, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleFrameAsync(ConnectorFrame frame, CancellationToken ct)
    {
        this.LastSeenUtc = this.timeProvider.GetUtcNow();

        switch (frame.Type)
        {
            case ConnectorFrameTypes.Inbound:
                await this.HandleInboundAsync(frame, ct).ConfigureAwait(false);
                break;

            case ConnectorFrameTypes.Pong:
                this.LogPongReceived(this.ChannelId ?? "?");
                break;

            case ConnectorFrameTypes.Abort:
                await this.HandleAbortAsync(frame, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleAbortAsync(ConnectorFrame frame, CancellationToken ct)
    {
        this.LogAbortReceived(this.ChannelId ?? "?");

        if (!ConnectorFrameParser.TryDeserializePayload<ConnectorAbortPayload>(frame, out var payload, out _))
        {
            return;
        }

        var channelId = this.ChannelId!;
        var conversationId = string.IsNullOrWhiteSpace(payload!.ConversationId)
            ? channelId
            : payload.ConversationId;

        // SECURITY: the agent aborts purely by conversation id and performs no ownership
        // check, so an unconstrained abort would let any local connector cancel a WebChat,
        // Discord, or rival connector's in-flight turn. Only conversations this session has
        // actually originated may be aborted.
        if (!this.OwnsConversation(conversationId))
        {
            this.LogAbortRejected(channelId, conversationId);

            // invalid_payload rather than protocol_violation: this is recoverable and the
            // session continues, and every protocol_violation the Bridge sends is fatal.
            // Keeping that split clean matters because connector authors key off it.
            await this.SendErrorFrameAsync(
                ConnectorErrorCodes.InvalidPayload,
                "abort refers to a conversation this connector does not own",
                ct).ConfigureAwait(false);
            return;
        }

        try
        {
            await this.abortDispatcher.AbortAsync(channelId, conversationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogAbortDispatchFailed(channelId, ex);
        }
    }

    /// <summary>
    /// A conversation belongs to this session when it is the channel's own id (the default,
    /// single-conversation case) or when this session has already forwarded an inbound message
    /// for it.
    /// </summary>
    private bool OwnsConversation(string conversationId)
    {
        if (string.Equals(conversationId, this.ChannelId, StringComparison.Ordinal))
        {
            return true;
        }

        lock (this.conversationLock)
        {
            return this.ownedConversations.Contains(conversationId);
        }
    }

    private void TrackConversation(string conversationId)
    {
        var capReached = false;
        lock (this.conversationLock)
        {
            // Bound the set so a connector cannot grow it without limit by cycling ids.
            // Refusing to track is fail-closed: an untracked conversation simply cannot be
            // aborted, so this can deny the connector its own abort but never authorises one.
            if (this.ownedConversations.Count >= MaxTrackedConversations)
            {
                capReached = !this.conversationCapLogged;
                this.conversationCapLogged = true;
            }
            else
            {
                this.ownedConversations.Add(conversationId);
            }
        }

        if (capReached)
        {
            this.LogConversationCapReached(this.ChannelId ?? "?", MaxTrackedConversations);
        }
    }

    private async Task HandleInboundAsync(ConnectorFrame frame, CancellationToken ct)
    {
        // Rate limiting applies exclusively to inbound frames; pong and abort bypass this check
        // so the limiter cannot break liveness or cancellation.
        if (!this.rateLimiter.TryAcquire())
        {
            var now = this.timeProvider.GetUtcNow();
            if (this.lastRateLimitLoggedAt is null || now - this.lastRateLimitLoggedAt.Value >= RateLimitLogSuppression)
            {
                this.lastRateLimitLoggedAt = now;
                this.LogRateLimited(this.ChannelId ?? "?", this.rateLimiter.MaxMessagesPerMinute);
            }

            await this.SendErrorFrameAsync(ConnectorErrorCodes.RateLimited, "Message rate limit exceeded.", ct).ConfigureAwait(false);
            return; // Continue the loop — do not close; a well-behaved connector should back off.
        }

        if (!ConnectorFrameParser.TryDeserializePayload<ConnectorInboundPayload>(frame, out var payload, out var err))
        {
            await this.SendErrorFrameAsync(ConnectorErrorCodes.InvalidPayload, err!, ct).ConfigureAwait(false);
            return;
        }

        var hasText = !string.IsNullOrWhiteSpace(payload!.Content?.Text);
        if (!hasText)
        {
            await this.SendErrorFrameAsync(
                ConnectorErrorCodes.InvalidPayload,
                "Inbound message must have text content.",
                ct).ConfigureAwait(false);
            return;
        }

        // Enforce negotiated maximum message length.
        var text = payload.Content!.Text!;
        if (text.Length > this.Channel!.Capabilities.MaxMessageLength)
        {
            await this.SendErrorFrameAsync(ConnectorErrorCodes.MessageTooLong, "Message text exceeds the negotiated maximum length.", ct).ConfigureAwait(false);
            return;
        }

        // Reject ids that exceed the maximum length — ids are identity and truncating them silently
        // would corrupt routing and audit trails.
        var rawMessageId = payload.MessageId;
        if (rawMessageId is not null && rawMessageId.Length > MaxIdLength)
        {
            await this.SendErrorFrameAsync(ConnectorErrorCodes.InvalidPayload, "messageId exceeds the maximum allowed length.", ct).ConfigureAwait(false);
            return;
        }

        var rawConversationId = payload.ConversationId;
        if (rawConversationId is not null && rawConversationId.Length > MaxIdLength)
        {
            await this.SendErrorFrameAsync(ConnectorErrorCodes.InvalidPayload, "conversationId exceeds the maximum allowed length.", ct).ConfigureAwait(false);
            return;
        }

        var rawSenderId = payload.Sender?.Id;
        if (rawSenderId is not null && rawSenderId.Length > MaxIdLength)
        {
            await this.SendErrorFrameAsync(ConnectorErrorCodes.InvalidPayload, "sender.id exceeds the maximum allowed length.", ct).ConfigureAwait(false);
            return;
        }

        var channelId = this.ChannelId!;

        // Truncate sender display name — cosmetic only, truncation is safe.
        var senderDisplayName = ConnectorText.Truncate(payload.Sender?.DisplayName, MaxDisplayNameLength);

        var message = new InboundMessage
        {
            MessageId = string.IsNullOrWhiteSpace(rawMessageId)
                ? Guid.NewGuid().ToString("n")
                : rawMessageId,
            ConversationId = string.IsNullOrWhiteSpace(rawConversationId)
                ? channelId
                : rawConversationId,
            ChannelId = channelId,
            ChannelType = ChannelType.Plugin,
            Sender = new SenderInfo
            {
                Id = rawSenderId ?? "connector",
                DisplayName = senderDisplayName,
                IsVerified = false,
            },
            Content = new MessageContent
            {
                Text = text,
                IsMarkdown = payload.Content?.IsMarkdown ?? false,
            },
            Timestamp = this.timeProvider.GetUtcNow(),
        };

        // Claiming the conversation here is what later authorises an abort for it.
        this.TrackConversation(message.ConversationId);

        await this.Channel!.ReceiveInboundAsync(message).ConfigureAwait(false);
    }

    private async Task<SendResult> OutboundSinkAsync(OutboundMessage message, CancellationToken ct)
    {
        if (!this.transport.IsOpen)
        {
            return SendResult.Error("transport is closed");
        }

        try
        {
            var payload = new ConnectorOutboundPayload
            {
                MessageId = message.MessageId,
                ConversationId = message.ConversationId,
                Content = new ConnectorContentPayload
                {
                    Text = message.Content.Text,
                    IsMarkdown = message.Content.IsMarkdown,
                },
                IsThinking = message.IsThinking,

                // The cursor MUST be the timestamp the agent recorded, not this process's
                // clock: the connector sends it back as sinceCursor and replay compares it
                // against stored timestamps. Using the send-time clock would silently skip
                // any message persisted between the store write and this dispatch.
                Cursor = ConnectorCursor.Format(message.Timestamp ?? this.timeProvider.GetUtcNow()),
            };
            var json = ConnectorFrame.Serialize(ConnectorFrameTypes.Outbound, payload);
            await this.transport.SendAsync(json, ct).ConfigureAwait(false);
            return SendResult.Ok(message.MessageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogOutboundSendFailed(message.MessageId, ex);
            return SendResult.Error(ex.Message);
        }
    }

    private async Task FrameSinkAsync(string json, CancellationToken ct)
    {
        if (!this.transport.IsOpen)
        {
            this.LogFrameSinkTransportClosed();
            return;
        }

        try
        {
            await this.transport.SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogSendFrameFailed("raw-frame", ex);
        }
    }

    private async Task<ConnectorAuthResult> ResolveAuthResultAsync(ConnectorAuthResult result, CancellationToken ct)
    {
        if (result.Outcome != ConnectorAuthOutcome.PairingRequired)
        {
            return result;
        }

        await this.SendFrameAsync(ConnectorFrameTypes.PairingRequired, new ConnectorPairingRequiredPayload
        {
            Code = result.PairingCode ?? string.Empty,
            ExpiresAt = result.ExpiresAt ?? this.timeProvider.GetUtcNow().AddMinutes(5),
        }, ct).ConfigureAwait(false);

        if (result.PairingCompletion is null)
        {
            return ConnectorAuthResult.Denied("pairing_unavailable");
        }

        ConnectorAuthResult completion;
        try
        {
            completion = await result.PairingCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            this.LogPairingCompletionError(ex);
            return ConnectorAuthResult.Denied("pairing error");
        }

        if (completion.Outcome == ConnectorAuthOutcome.Approved)
        {
            return completion;
        }

        return ConnectorAuthResult.Denied(completion.Reason ?? "pairing denied");
    }

    private void OnPingTimerTickAsync(object? state)
    {
        _ = this.PingTimerTickAsync();
    }

    private async Task PingTimerTickAsync()
    {
        lock (this.teardownLock)
        {
            // The timer callback can be in flight when teardown disposes the transport.
            if (this.tornDown)
            {
                return;
            }
        }

        var now = this.timeProvider.GetUtcNow();
        var lastSeen = this.LastSeenUtc;
        if (lastSeen.HasValue && (now - lastSeen.Value).TotalSeconds >= HeartbeatTimeoutSeconds)
        {
            this.LogHeartbeatTimeout(this.ChannelId ?? "?", HeartbeatTimeoutSeconds);
            await this.SendErrorAndCloseAsync(
                ConnectorErrorCodes.ProtocolViolation,
                "heartbeat_timeout",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var json = ConnectorFrame.Serialize(ConnectorFrameTypes.Ping);
        try
        {
            await this.transport.SendAsync(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogSendFrameFailed(ConnectorFrameTypes.Ping, ex);
        }
    }

    private async Task SendFrameAsync<TPayload>(string type, TPayload payload, CancellationToken ct)
    {
        var json = ConnectorFrame.Serialize(type, payload);
        try
        {
            await this.transport.SendAsync(json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogSendFrameFailed(type, ex);
        }
    }

    private async Task SendErrorFrameAsync(string code, string message, CancellationToken ct)
    {
        await this.SendFrameAsync(ConnectorFrameTypes.Error, new ConnectorErrorPayload
        {
            Code = code,
            Message = message,
        }, ct).ConfigureAwait(false);
    }

    private async Task SendErrorAndCloseAsync(string code, string message, CancellationToken ct)
    {
        await this.SendErrorFrameAsync(code, message, ct).ConfigureAwait(false);
        await this.transport.CloseAsync(message, ct).ConfigureAwait(false);
    }

    private async Task TeardownAsync()
    {
        lock (this.teardownLock)
        {
            if (this.tornDown)
            {
                return;
            }

            this.tornDown = true;
        }

        if (this.pingTimer is not null)
        {
            await this.pingTimer.DisposeAsync().ConfigureAwait(false);
            this.pingTimer = null;
        }

        if (this.Channel is not null)
        {
            await this.registry.DetachAsync(this.Channel).ConfigureAwait(false);
        }

        await this.transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.TeardownAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connector peer closed connection before sending hello. Endpoint: {RemoteEndpoint}")]
    private partial void LogPeerClosedBeforeHello(string remoteEndpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connector session ready: channelId={ChannelId}, remote={RemoteEndpoint}")]
    private partial void LogSessionReady(string channelId, string remoteEndpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unexpected connector session error.")]
    private partial void LogUnexpectedSessionError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Authenticator threw an unexpected exception.")]
    private partial void LogAuthenticatorError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pairing completion task faulted.")]
    private partial void LogPairingCompletionError(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Read loop faulted.")]
    private partial void LogReadLoopFault(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send frame type '{FrameType}'.")]
    private partial void LogSendFrameFailed(string frameType, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to send outbound message {MessageId}.")]
    private partial void LogOutboundSendFailed(string messageId, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pong received from channel {ChannelId}.")]
    private partial void LogPongReceived(string channelId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Abort received from channel {ChannelId} (Phase 3 handling).")]
    private partial void LogAbortReceived(string channelId);

    /// <summary>
    /// Logged at Warning, suppressed to at most once per minute per session so a hammering
    /// connector cannot itself become a log-flood vector.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} exceeded rate limit of {MaxMessagesPerMinute} messages/min; further messages dropped until window slides.")]
    private partial void LogRateLimited(string channelId, int maxMessagesPerMinute);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat timeout: channel {ChannelId} has not sent any frame within {TimeoutSeconds}s.")]
    private partial void LogHeartbeatTimeout(string channelId, int timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Abort dispatch failed for channel {ChannelId}.")]
    private partial void LogAbortDispatchFailed(string channelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} attempted to abort conversation {ConversationId} which it does not own; rejected.")]
    private partial void LogAbortRejected(string channelId, string conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} reached the tracked-conversation cap of {MaxTrackedConversations}; further conversations cannot be aborted by it.")]
    private partial void LogConversationCapReached(string channelId, int maxTrackedConversations);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Frame sink: transport is closed, dropping frame.")]
    private partial void LogFrameSinkTransportClosed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connector {ChannelId}: sinceCursor is {Reason}, replay skipped.")]
    private partial void LogNoCursor(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId}: replay source threw an unexpected exception; attaching without replay.")]
    private partial void LogReplaySourceFailed(string channelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId}: failed to send replay frame for message {MessageId}; aborting replay.")]
    private partial void LogReplayFrameFailed(string channelId, string messageId, Exception ex);
}

