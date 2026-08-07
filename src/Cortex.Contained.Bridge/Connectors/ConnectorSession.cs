using Cortex.Contained.Bridge.Connectors.Protocol;
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
    private readonly IConnectorTransport transport;
    private readonly IConnectorAuthenticator authenticator;
    private readonly ConnectorSettingsConfig settings;
    private readonly IConnectorRegistry registry;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<ConnectorSession> logger;
    private readonly TimeProvider timeProvider;
    private readonly Lock teardownLock = new();
    private bool tornDown;

    /// <summary>The plugin channel created during the handshake phase.</summary>
    public PluginChannel? Channel { get; private set; }

    /// <summary>The channel id assigned during the handshake phase.</summary>
    public string? ChannelId => this.Channel?.ChannelId;

    /// <summary>Timestamp of the last <c>pong</c> received from the connector.</summary>
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
        TimeProvider timeProvider)
    {
        this.transport = transport;
        this.authenticator = authenticator;
        this.settings = settings;
        this.registry = registry;
        this.loggerFactory = loggerFactory;
        this.logger = loggerFactory.CreateLogger<ConnectorSession>();
        this.timeProvider = timeProvider;
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

    // ── Handshake + read loop ────────────────────────────────────────

    private async Task RunInternalAsync(CancellationToken ct)
    {
        // 1. Read the first frame (peer may close immediately).
        string? firstJson;
        try
        {
            firstJson = await this.transport.ReceiveAsync(ct).ConfigureAwait(false);
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

        // 2. Parse the frame.
        if (!ConnectorFrameParser.TryParse(firstJson, out var frame, out var parseErrorCode, out var parseErrorMessage))
        {
            await this.SendErrorAndCloseAsync(parseErrorCode!, parseErrorMessage!, ct).ConfigureAwait(false);
            return;
        }

        // 3. First frame must be hello.
        if (frame!.Type != ConnectorFrameTypes.Hello)
        {
            await this.SendErrorAndCloseAsync(
                ConnectorErrorCodes.ProtocolViolation,
                $"Expected hello frame, got '{frame.Type}'.",
                ct).ConfigureAwait(false);
            return;
        }

        // 4. Deserialise hello payload and normalise fields.
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

        // A null/whitespace instanceId defaults to "default" BEFORE normalising.
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
        var displayName = string.IsNullOrWhiteSpace(hello.DisplayName) ? normalizedKey : hello.DisplayName;

        // 5. Authenticate.
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

        // Resolve PairingRequired if it comes back.
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

        // 6. Send paired frame (with token) BEFORE ready, when a token was issued.
        if (authResult.IssuedToken is not null)
        {
            await this.SendFrameAsync(ConnectorFrameTypes.Paired, new ConnectorPairedPayload
            {
                Token = authResult.IssuedToken,
                ChannelId = channelId,
            }, ct).ConfigureAwait(false);
        }

        // 7. Build ChannelCapabilities from the hello payload.
        var caps = hello.Capabilities;
        var capabilities = new ChannelCapabilities
        {
            SupportsStreaming = caps?.Streaming ?? false,
            SupportsRichText = caps?.RichText ?? false,
            // v1 never enables media even if the connector requests it — media support is reserved for v2+
            SupportsMedia = false,
            MaxMessageLength = Math.Clamp(caps?.MaxMessageLength ?? 100_000, 1, 100_000),
        };

        // 8. Create channel, set sink, attach to registry.
        var pluginChannel = new PluginChannel(
            normalizedKey,
            normalizedInstanceId,
            capabilities,
            displayName,
            this.loggerFactory.CreateLogger<PluginChannel>());

        pluginChannel.OutboundSink = this.OutboundSinkAsync;

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

        // 9. Send ready.
        await this.SendFrameAsync(ConnectorFrameTypes.Ready, new ConnectorReadyPayload
        {
            ChannelId = channelId,
            ReplayCount = 0, // Phase 4 fills replay in
        }, ct).ConfigureAwait(false);

        // 10. Read loop.
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
        switch (frame.Type)
        {
            case ConnectorFrameTypes.Inbound:
                await this.HandleInboundAsync(frame, ct).ConfigureAwait(false);
                break;

            case ConnectorFrameTypes.Pong:
                this.LastSeenUtc = this.timeProvider.GetUtcNow();
                this.LogPongReceived(this.ChannelId ?? "?");
                break;

            case ConnectorFrameTypes.Abort:
                // Phase 3 — parse and log only; do not error.
                this.LogAbortReceived(this.ChannelId ?? "?");
                break;
        }
    }

    private async Task HandleInboundAsync(ConnectorFrame frame, CancellationToken ct)
    {
        if (!ConnectorFrameParser.TryDeserializePayload<ConnectorInboundPayload>(frame, out var payload, out var err))
        {
            // Bad payload — send error but CONTINUE the loop; a bad message must not kill the session.
            await this.SendErrorFrameAsync(ConnectorErrorCodes.InvalidPayload, err!, ct).ConfigureAwait(false);
            return;
        }

        // A missing/whitespace content.text AND no attachments → invalid payload; CONTINUE.
        // v1 never has attachments (media is reserved for v2+), so reject if text is absent.
        var hasText = !string.IsNullOrWhiteSpace(payload!.Content?.Text);
        if (!hasText)
        {
            await this.SendErrorFrameAsync(
                ConnectorErrorCodes.InvalidPayload,
                "Inbound message must have text content.",
                ct).ConfigureAwait(false);
            return;
        }

        var channelId = this.ChannelId!;

        // IsGroup and ThreadId are reserved for first-party channels in v1 — never set from connector input.
        var message = new InboundMessage
        {
            MessageId = string.IsNullOrWhiteSpace(payload.MessageId)
                ? Guid.NewGuid().ToString("n")
                : payload.MessageId,
            ConversationId = string.IsNullOrWhiteSpace(payload.ConversationId)
                ? channelId
                : payload.ConversationId,
            ChannelId = channelId,
            ChannelType = ChannelType.Plugin,
            Sender = new SenderInfo
            {
                Id = payload.Sender?.Id ?? "connector",
                DisplayName = payload.Sender?.DisplayName,
                IsVerified = false,
            },
            Content = new MessageContent
            {
                Text = payload.Content?.Text,
                IsMarkdown = payload.Content?.IsMarkdown ?? false,
            },
            Timestamp = this.timeProvider.GetUtcNow(),
        };

        await this.Channel!.ReceiveInboundAsync(message).ConfigureAwait(false);
    }

    // ── Outbound sink ────────────────────────────────────────────────

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

    // ── Auth helpers ─────────────────────────────────────────────────

    private async Task<ConnectorAuthResult> ResolveAuthResultAsync(ConnectorAuthResult result, CancellationToken ct)
    {
        if (result.Outcome != ConnectorAuthOutcome.PairingRequired)
        {
            return result;
        }

        // Always send the pairing_required frame so the connector can display the code,
        // even when PairingCompletion is null (which means the service is unavailable).
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

    // ── Frame helpers ────────────────────────────────────────────────

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

    // ── Teardown ─────────────────────────────────────────────────────

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

        if (this.Channel is not null)
        {
            // DetachAsync owns disconnecting the channel, so it is not disconnected here too.
            await this.registry.DetachAsync(this.Channel).ConfigureAwait(false);
        }

        await this.transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.TeardownAsync().ConfigureAwait(false);
    }

    // ── Logging ──────────────────────────────────────────────────────

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
}
