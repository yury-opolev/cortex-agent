using System.Text;
using Cortex.Contained.Bridge.Connectors.Media;
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

    /// <summary>
    /// Bytes held back from the outbound frame budget after the envelope has been measured.
    /// Covers the JSON structure the attachment array itself adds (the field name, brackets and
    /// separators) plus slack for any estimate being marginally optimistic.
    /// </summary>
    /// <remarks>
    /// Exceeding the frame cap outbound is not fatal — <see cref="WebSocketConnectorTransport"/>
    /// throws, the session survives, and the message is dropped with a logged error. That is
    /// still a lost message, so the budget aims to make it unreachable rather than merely
    /// survivable.
    /// </remarks>
    internal const int OutboundFrameSafetyMarginBytes = 4096;

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
    private readonly ConnectorMediaPolicy mediaPolicy;
    private readonly ConnectorAttachmentValidator attachmentValidator;
    private readonly ConnectorOutboundAttachmentProjector outboundAttachmentProjector;
    private readonly IConnectorAttachmentResolver? attachmentResolver;
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
    /// <param name="transport">The connector's transport.</param>
    /// <param name="authenticator">Pairing and token authentication.</param>
    /// <param name="settings">Connector subsystem settings.</param>
    /// <param name="registry">Registry the negotiated channel attaches to.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="timeProvider">Time source for heartbeats, rate limiting and timestamps.</param>
    /// <param name="abortDispatcher">Dispatcher for abort requests.</param>
    /// <param name="replaySource">Source of missed messages on reattach.</param>
    /// <param name="attachmentResolver">
    /// Resolves attachment handles to bytes. Null means the Bridge is holding no uploaded
    /// content, so every handle a connector presents is treated as unknown.
    /// </param>
    /// <param name="attachmentIssuer">
    /// Issues handles for outbound attachments too large to inline. Null means oversized
    /// outbound attachments are dropped rather than carried.
    /// </param>
    public ConnectorSession(
        IConnectorTransport transport,
        IConnectorAuthenticator authenticator,
        ConnectorSettingsConfig settings,
        IConnectorRegistry registry,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IConnectorAbortDispatcher abortDispatcher,
        IConnectorReplaySource replaySource,
        IConnectorAttachmentResolver? attachmentResolver = null,
        IConnectorAttachmentIssuer? attachmentIssuer = null)
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
        this.attachmentResolver = attachmentResolver;
        this.rateLimiter = new ConnectorRateLimiter(settings.Limits.MaxMessagesPerMinute, timeProvider);
        this.mediaPolicy = ConnectorMediaPolicy.From(settings.Media, settings.Limits.MaxFrameBytes);
        this.attachmentValidator = new ConnectorAttachmentValidator(this.mediaPolicy);
        this.outboundAttachmentProjector = new ConnectorOutboundAttachmentProjector(this.mediaPolicy, attachmentIssuer);
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

            // The operator kill-switch beats the connector's own declaration: a connector can
            // only opt IN to media, never enable it when policy has turned it off.
            SupportsMedia = this.mediaPolicy.Enabled && (caps?.Media ?? false),
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
        var hasAttachments = payload.Content?.Attachments is { Count: > 0 };
        if (!hasText && !hasAttachments)
        {
            await this.SendErrorFrameAsync(
                ConnectorErrorCodes.InvalidPayload,
                "Inbound message must have text content or at least one attachment.",
                ct).ConfigureAwait(false);
            return;
        }

        // Enforce negotiated maximum message length. An attachment-only message has no text,
        // which is legal — the agent's own validator accepts it too.
        var text = payload.Content?.Text ?? string.Empty;
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

        // Attachments are validated after the id checks so a malformed id is still the first
        // thing reported, and before the message is built so nothing unvalidated can reach the
        // agent. Every failure here is NON-FATAL: a bad attachment must not kill a live session.
        var attachmentResult = this.attachmentValidator.Validate(
            payload.Content?.Attachments,
            this.Channel!.Capabilities.SupportsMedia);

        if (!attachmentResult.Success)
        {
            await this.SendErrorFrameAsync(
                attachmentResult.ErrorCode!,
                attachmentResult.ErrorMessage!,
                ct).ConfigureAwait(false);
            return;
        }

        if (!this.TryMaterialiseAttachments(attachmentResult.Attachments, channelId, out var attachments))
        {
            await this.SendErrorFrameAsync(
                ConnectorErrorCodes.AttachmentNotFound,
                "An attachment handle is unknown, expired, or was issued to another channel.",
                ct).ConfigureAwait(false);
            return;
        }

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
                Attachments = attachments,
            },
            Timestamp = this.timeProvider.GetUtcNow(),
        };

        // Claiming the conversation here is what later authorises an abort for it.
        this.TrackConversation(message.ConversationId);

        await this.Channel!.ReceiveInboundAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts validated attachments into <see cref="MediaAttachment"/>s, resolving any that
    /// arrived as handles. Returns false when a handle cannot be resolved, so the caller can
    /// refuse the whole message rather than silently deliver it with an attachment missing.
    /// </summary>
    private bool TryMaterialiseAttachments(
        IReadOnlyList<ValidatedAttachment> validated,
        string channelId,
        out IReadOnlyList<MediaAttachment>? attachments)
    {
        attachments = null;

        if (validated.Count == 0)
        {
            return true;
        }

        var materialised = new List<MediaAttachment>(validated.Count);

        foreach (var attachment in validated)
        {
            var mimeType = attachment.MimeType;
            var fileName = attachment.FileName;
            var caption = attachment.Caption;
            var data = attachment.Data;

            if (data is null)
            {
                // Arrived as a handle. Without a resolver the Bridge is not holding any uploaded
                // content, so the handle is by definition unknown.
                var content = this.attachmentResolver?.Resolve(attachment.Handle!, channelId);
                if (content is null)
                {
                    this.LogAttachmentHandleUnresolved(channelId);
                    return false;
                }

                // Defence in depth. The store validated this content when it was uploaded, but
                // policy can be NARROWED afterwards, and a store bug must not be the only thing
                // standing between a connector and a disallowed type. Re-checking on read costs
                // an allow-list lookup and a 12-byte signature comparison.
                if (!this.mediaPolicy.IsMimeTypeAllowed(content.MimeType)
                    || !ImageContentSniffer.MatchesDeclaredType(content.Data, content.MimeType))
                {
                    this.LogAttachmentHandleContentRejected(channelId);
                    return false;
                }

                data = content.Data;
                mimeType = content.MimeType;

                // Frame-supplied metadata wins when present: the connector may caption an
                // upload at send time rather than at upload time.
                fileName ??= content.FileName;
                caption ??= content.Caption;
            }

            materialised.Add(new MediaAttachment
            {
                MimeType = mimeType,
                FileName = fileName,
                Caption = caption,
                Data = data,
                SizeBytes = data.LongLength,
            });
        }

        attachments = materialised;
        return true;
    }

    private async Task<SendResult> OutboundSinkAsync(OutboundMessage message, CancellationToken ct)
    {
        if (!this.transport.IsOpen)
        {
            return SendResult.Error("transport is closed");
        }

        try
        {
            var cursor = ConnectorCursor.Format(message.Timestamp ?? this.timeProvider.GetUtcNow());

            // Measure the frame WITHOUT attachments first. A fixed reserve cannot work here:
            // message text alone may be up to MaxMessageLength characters, which dwarfs any
            // constant we could pick. Budgeting from the real envelope is what makes
            // "attachments never overflow the frame" true rather than merely likely.
            var skeleton = new ConnectorOutboundPayload
            {
                MessageId = message.MessageId,
                ConversationId = message.ConversationId,
                Content = new ConnectorContentPayload
                {
                    Text = message.Content.Text,
                    IsMarkdown = message.Content.IsMarkdown,
                },
                IsThinking = message.IsThinking,
                Cursor = cursor,
            };

            var envelopeBytes = Encoding.UTF8.GetByteCount(
                ConnectorFrame.Serialize(ConnectorFrameTypes.Outbound, skeleton));

            var inlineBudget = (int)Math.Clamp(
                this.settings.Limits.MaxFrameBytes - envelopeBytes - OutboundFrameSafetyMarginBytes,
                0,
                int.MaxValue);

            var attachmentProjection = this.outboundAttachmentProjector.Project(
                message.Content.Attachments,
                this.ChannelId ?? string.Empty,
                this.Channel?.Capabilities.SupportsMedia ?? false,
                inlineBudget);

            if (attachmentProjection.DroppedCount > 0)
            {
                this.LogOutboundAttachmentsDropped(
                    this.ChannelId ?? "?",
                    attachmentProjection.DroppedCount,
                    message.MessageId);
            }

            var payload = new ConnectorOutboundPayload
            {
                MessageId = message.MessageId,
                ConversationId = message.ConversationId,
                Content = new ConnectorContentPayload
                {
                    Text = message.Content.Text,
                    IsMarkdown = message.Content.IsMarkdown,
                    Attachments = attachmentProjection.Attachments,
                },
                IsThinking = message.IsThinking,

                // The cursor MUST be the timestamp the agent recorded, not this process's
                // clock: the connector sends it back as sinceCursor and replay compares it
                // against stored timestamps. Using the send-time clock would silently skip
                // any message persisted between the store write and this dispatch.
                Cursor = cursor,
            };
            var json = ConnectorFrame.Serialize(ConnectorFrameTypes.Outbound, payload);
            await this.transport.SendAsync(json, ct).ConfigureAwait(false);
            return SendResult.Ok(message.MessageId);
        }
        catch (ConnectorFrameTooLargeException ex)
        {
            // The attachment budget cannot prevent this on its own: a message whose TEXT alone
            // exceeds the frame cap is already too large before any attachment is considered.
            // The transport backstop caught it, so the session survives and only this message is
            // lost — but it is lost silently from the connector's point of view, so say so
            // clearly here rather than letting it look like a network fault.
            this.LogOutboundFrameTooLarge(this.ChannelId ?? "?", message.MessageId, ex.MaxFrameBytes);
            return SendResult.Error($"outbound frame exceeds the {ex.MaxFrameBytes}-byte limit");
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
        // Fire and forget on a timer thread: an escaping exception here would surface as an
        // unobserved task exception, so the async body must swallow everything itself.
        _ = this.PingTimerTickAsync();
    }

    private async Task PingTimerTickAsync()
    {
        try
        {
            await this.PingTimerTickCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogHeartbeatTickFailed(ex);
        }
    }

    private async Task PingTimerTickCoreAsync()
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Abort received from channel {ChannelId}.")]
    private partial void LogAbortReceived(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector heartbeat tick failed.")]
    private partial void LogHeartbeatTickFailed(Exception ex);

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

    /// <summary>
    /// Logged without the handle value: a handle is a bearer capability, and the four reasons a
    /// lookup fails (unknown, expired, consumed, wrong channel) are deliberately not distinguished
    /// so the log cannot be used to confirm another connector's attachment exists.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} referenced an attachment handle that could not be resolved; message rejected.")]
    private partial void LogAttachmentHandleUnresolved(string channelId);

    /// <summary>
    /// Fires when stored content resolves but no longer satisfies the current media policy —
    /// normally because the allow-list was narrowed after the upload, otherwise a store defect.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId} referenced stored attachment content that is not allowed by the current media policy; message rejected.")]
    private partial void LogAttachmentHandleContentRejected(string channelId);

    /// <summary>
    /// Fires when the agent sent attachments that could not be delivered — the connector does not
    /// support media, the type is not allowed, or the content is too large to inline with no
    /// out-of-band channel available. The message itself is still delivered, minus the media.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId}: {DroppedCount} outbound attachment(s) on message {MessageId} could not be delivered and were dropped.")]
    private partial void LogOutboundAttachmentsDropped(string channelId, int droppedCount, string messageId);

    /// <summary>
    /// Fires when a whole outbound message is too large for a frame, which after attachment
    /// budgeting means its text alone overflows the cap. The session survives; the message does not.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Connector {ChannelId}: outbound message {MessageId} exceeds the {MaxFrameBytes}-byte frame limit and was NOT delivered. The session remains open.")]
    private partial void LogOutboundFrameTooLarge(string channelId, string messageId, int maxFrameBytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Frame sink: transport is closed, dropping frame.")]
    private partial void LogFrameSinkTransportClosed();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connector {ChannelId}: sinceCursor is {Reason}, replay skipped.")]
    private partial void LogNoCursor(string channelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId}: replay source threw an unexpected exception; attaching without replay.")]
    private partial void LogReplaySourceFailed(string channelId, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connector {ChannelId}: failed to send replay frame for message {MessageId}; aborting replay.")]
    private partial void LogReplayFrameFailed(string channelId, string messageId, Exception ex);
}

