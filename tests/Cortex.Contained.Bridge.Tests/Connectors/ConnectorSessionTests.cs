// NSubstitute setup calls for ValueTask-returning methods trigger CA2012 because the
// analyzer doesn't recognise .Returns() as a consumer of the returned ValueTask.
// This suppression is intentional and scoped to the test helper/setup code only.
#pragma warning disable CA2012
using System.Text.Json;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Bridge.Connectors.Replay;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorSessionTests
{
    private static ConnectorSettingsConfig DefaultSettings() => new()
    {
        Enabled = true,
        MaxConnectors = 16,
    };

    private static string HelloFrame(
        string key = "terminal",
        string? instanceId = null,
        string? token = null,
        string? displayName = null,
        string? sinceCursor = null) =>
        ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = key,
            InstanceId = instanceId,
            Token = token,
            DisplayName = displayName,
            SinceCursor = sinceCursor,
        });

    private static ConnectorSession BuildSession(
        IConnectorTransport transport,
        IConnectorAuthenticator? authenticator = null,
        IConnectorRegistry? registry = null,
        ConnectorSettingsConfig? settings = null,
        TimeProvider? timeProvider = null,
        IConnectorAbortDispatcher? abortDispatcher = null,
        IConnectorReplaySource? replaySource = null)
    {
        if (authenticator is null)
        {
            authenticator = Substitute.For<IConnectorAuthenticator>();
            authenticator.AuthenticateAsync(Arg.Any<ConnectorAuthRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => ValueTask.FromResult(ConnectorAuthResult.Approved("tok-123")));
        }

        if (registry is null)
        {
            registry = Substitute.For<IConnectorRegistry>();
            registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
                .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
            registry.DetachAsync(Arg.Any<PluginChannel>())
                .Returns(_ => ValueTask.CompletedTask);
        }

        abortDispatcher ??= Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        if (replaySource is null)
        {
            replaySource = Substitute.For<IConnectorReplaySource>();
            replaySource.GetMissedMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<ConnectorReplayMessage>>([]));
        }
        return new ConnectorSession(
            transport,
            authenticator,
            settings ?? DefaultSettings(),
            registry,
            NullLoggerFactory.Instance,
            timeProvider ?? TimeProvider.System,
            abortDispatcher,
            replaySource);
    }

    // ── 1. Peer closes without hello ─────────────────────────────────

    [Fact]
    public async Task RunAsync_PeerClosesBeforeHello_CleanExit()
    {
        var transport = new FakeConnectorTransport();
        transport.CompleteIncoming();
        var session = BuildSession(transport);

        await session.RunAsync(CancellationToken.None);

        Assert.Empty(transport.Sent);
        Assert.Null(session.Channel);
    }

    // ── 2. First frame is not hello ──────────────────────────────────

    [Fact]
    public async Task RunAsync_FirstFrameNotHello_SendsProtocolViolationError()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Content = new ConnectorContentPayload { Text = "hi" },
        }));
        transport.CompleteIncoming();
        var session = BuildSession(transport);

        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("protocol_violation"));
    }

    // ── 3. Parse error ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_MalformedFirstFrame_SendsMalformedFrameError()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming("not-json-at-all{{{");
        transport.CompleteIncoming();
        var session = BuildSession(transport);

        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("malformed_frame"));
    }

    // ── 4. Invalid key / instanceId ──────────────────────────────────

    [Theory]
    [InlineData("", "default", "invalid_payload")]    // empty key
    [InlineData("!!bad!!", "default", "invalid_payload")]  // bad chars in key
    [InlineData("terminal", "!!bad!!", "invalid_payload")] // bad chars in instanceId
    public async Task RunAsync_InvalidKeyOrInstanceId_SendsInvalidPayload(
        string key, string instanceId, string expectedCode)
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(key: key, instanceId: instanceId));
        transport.CompleteIncoming();
        var session = BuildSession(transport);

        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains(expectedCode));
    }

    // ── 4b. Null instanceId defaults to "default" ────────────────────

    [Fact]
    public async Task RunAsync_NullInstanceId_DefaultsToDefault()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(key: "terminal", instanceId: null));
        transport.CompleteIncoming();

        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.Equal("plugin:terminal:default", session.ChannelId);
    }

    // ── 5. Denied auth ───────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AuthDenied_SendsPairingDenied()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.CompleteIncoming();

        var auth = Substitute.For<IConnectorAuthenticator>();
        auth.AuthenticateAsync(Arg.Any<ConnectorAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAuthResult.Denied("not allowed")));

        var session = BuildSession(transport, authenticator: auth);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("pairing_denied"));
        Assert.Null(session.Channel);
    }

    // ── 5b. PairingRequired with null completion → denied ────────────

    [Fact]
    public async Task RunAsync_PairingRequiredNullCompletion_SendsPairingDenied()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.CompleteIncoming();

        var auth = Substitute.For<IConnectorAuthenticator>();
        auth.AuthenticateAsync(Arg.Any<ConnectorAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(new ConnectorAuthResult
            {
                Outcome = ConnectorAuthOutcome.PairingRequired,
                PairingCode = "ABC123",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                PairingCompletion = null, // null → pairing_unavailable
            }));

        var session = BuildSession(transport, authenticator: auth);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("pairing_required"));
        Assert.Contains(transport.Sent, f => f.Contains("pairing_denied"));
        Assert.Null(session.Channel);
    }

    // ── 5c. PairingRequired → completion → approved ──────────────────

    [Fact]
    public async Task RunAsync_PairingRequiredThenApproved_CompletesHandshake()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.CompleteIncoming();

        var tcs = new TaskCompletionSource<ConnectorAuthResult>();
        tcs.SetResult(ConnectorAuthResult.Approved("newtoken"));

        var auth2 = Substitute.For<IConnectorAuthenticator>();
        auth2.AuthenticateAsync(Arg.Any<ConnectorAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(new ConnectorAuthResult
            {
                Outcome = ConnectorAuthOutcome.PairingRequired,
                PairingCode = "ABC123",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                PairingCompletion = tcs.Task,
            }));

        var registry2 = Substitute.For<IConnectorRegistry>();
        registry2.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry2.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, authenticator: auth2, registry: registry2);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("pairing_required"));
        Assert.Contains(transport.Sent, f => f.Contains("paired"));
        Assert.Contains(transport.Sent, f => f.Contains("ready"));
        Assert.NotNull(session.Channel);
    }

    // ── 6. Paired frame sent when token is issued ────────────────────

    [Fact]
    public async Task RunAsync_ApprovedWithToken_SendsPairedBeforeReady()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.CompleteIncoming();
        var session = BuildSession(transport);

        await session.RunAsync(CancellationToken.None);

        var pairedIdx = transport.Sent.FindIndex(f => f.Contains("\"paired\""));
        var readyIdx = transport.Sent.FindIndex(f => f.Contains("\"ready\""));
        Assert.True(pairedIdx >= 0, "paired frame not found");
        Assert.True(readyIdx >= 0, "ready frame not found");
        Assert.True(pairedIdx < readyIdx, "paired must come before ready");
    }

    // ── 7. Capabilities clamped / media honoured ─────────────────────

    [Fact]
    public async Task RunAsync_MediaCapabilityRequested_IsHonoured()
    {
        var captured = await AttachAndCaptureChannelAsync(
            new ConnectorCapabilitiesPayload { Media = true, MaxMessageLength = 200_000 });

        Assert.True(captured.Capabilities.SupportsMedia);
        Assert.Equal(100_000, captured.Capabilities.MaxMessageLength); // clamped
    }

    [Fact]
    public async Task RunAsync_MediaCapabilityNotRequested_StaysDisabled()
    {
        var captured = await AttachAndCaptureChannelAsync(
            new ConnectorCapabilitiesPayload { Media = false });

        Assert.False(captured.Capabilities.SupportsMedia);
    }

    [Fact]
    public async Task RunAsync_CapabilitiesAbsent_MediaStaysDisabled()
    {
        var captured = await AttachAndCaptureChannelAsync(capabilities: null);

        Assert.False(captured.Capabilities.SupportsMedia);
    }

    [Fact]
    public async Task RunAsync_MediaRequestedButDisabledByConfig_StaysDisabled()
    {
        // The operator kill-switch must beat a connector's own declaration, otherwise
        // `connectors.media.enabled: false` would not actually turn anything off.
        var settings = DefaultSettings();
        settings.Media = new ConnectorMediaConfig { Enabled = false };

        var captured = await AttachAndCaptureChannelAsync(
            new ConnectorCapabilitiesPayload { Media = true },
            settings);

        Assert.False(captured.Capabilities.SupportsMedia);
    }

    private static async Task<PluginChannel> AttachAndCaptureChannelAsync(
        ConnectorCapabilitiesPayload? capabilities,
        ConnectorSettingsConfig? settings = null)
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Capabilities = capabilities,
        }));
        transport.CompleteIncoming();

        var registry = Substitute.For<IConnectorRegistry>();
        PluginChannel? captured = null;
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch => captured = ch), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry, settings: settings);
        await session.RunAsync(CancellationToken.None);

        Assert.NotNull(captured);
        return captured!;
    }

    [Fact]
    public async Task RunAsync_NullCapabilities_DoesNotThrow()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Capabilities = null,
        }));
        transport.CompleteIncoming();

        var session = BuildSession(transport);
        var ex = await Record.ExceptionAsync(() => session.RunAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    // ── 8. Registry failure ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_RegistryRejectsChannel_SendsErrorAndDoesNotRegister()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.CompleteIncoming();

        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Failed(ConnectorErrorCodes.Duplicate, "duplicate")));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("duplicate_connector") || f.Contains("error"));
        Assert.Null(session.Channel);
    }

    // ── 9. Ready frame sent ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SuccessfulHandshake_SendsReadyWithChannelId()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(key: "terminal", instanceId: "main"));
        transport.CompleteIncoming();
        var session = BuildSession(transport);

        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("ready") && f.Contains("plugin:terminal:main"));
    }

    // ── 10a. Inbound message routed to channel ───────────────────────

    [Fact]
    public async Task RunAsync_InboundFrame_DeliveredToChannel()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            MessageId = "msg-1",
            Content = new ConnectorContentPayload { Text = "hello bot" },
        }));
        transport.CompleteIncoming();

        InboundMessage? received = null;
        var registry = Substitute.For<IConnectorRegistry>();
        PluginChannel? capturedChannel = null;
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            capturedChannel = ch;
            ch.MessageReceived += msg =>
            {
                received = msg;
                return Task.CompletedTask;
            };
        }), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("hello bot", received!.Content.Text);
        Assert.Equal("msg-1", received.MessageId);
    }

    // ── 10b. Inbound with empty text → error but loop continues ──────

    [Fact]
    public async Task RunAsync_InboundEmptyText_SendsErrorButContinues()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Content = new ConnectorContentPayload { Text = "  " }, // whitespace only
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Content = new ConnectorContentPayload { Text = "real message" },
        }));
        transport.CompleteIncoming();

        var messages = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg =>
            {
                messages.Add(msg);
                return Task.CompletedTask;
            };
        }), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.Single(messages); // only the second (valid) message
        Assert.Contains(transport.Sent, f => f.Contains("invalid_payload"));
    }

    // ── 10c. Inbound field defaults ──────────────────────────────────

    [Fact]
    public async Task RunAsync_InboundMissingFields_DefaultsApplied()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(key: "bot", instanceId: "main"));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            // No messageId, conversationId, sender
            Content = new ConnectorContentPayload { Text = "hi" },
        }));
        transport.CompleteIncoming();

        InboundMessage? received = null;
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received = msg; return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("plugin:bot:main", received!.ConversationId); // defaults to channel id
        Assert.Equal("connector", received.Sender.Id);
        Assert.False(string.IsNullOrEmpty(received.MessageId)); // auto-generated GUID
    }

    [Fact]
    public async Task RunAsync_AbortForForeignConversation_IsRejectedAndNotDispatched()
    {
        // A hostile connector must not be able to cancel a WebChat, Discord, or rival
        // connector's in-flight turn: the agent aborts purely by conversation id and does
        // no ownership check of its own.
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("abort", new ConnectorAbortPayload
        {
            ConversationId = "webchat-default",
        }));
        transport.CompleteIncoming();

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = BuildSession(transport, abortDispatcher: abortDispatcher);
        await session.RunAsync(CancellationToken.None);

        await abortDispatcher.DidNotReceive().AbortAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        Assert.Contains(transport.Sent, f => f.Contains("invalid_payload"));
    }

    [Fact]
    public async Task RunAsync_AbortForOwnChannelId_IsAllowedWithoutPriorInbound()
    {
        // The single-conversation case: conversationId == channelId is owned by definition,
        // so a connector can abort its own default conversation before sending anything.
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("abort", new ConnectorAbortPayload
        {
            ConversationId = "plugin:terminal:default",
        }));
        transport.CompleteIncoming();

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = BuildSession(transport, abortDispatcher: abortDispatcher);
        await session.RunAsync(CancellationToken.None);

        await abortDispatcher.Received(1).AbortAsync(
            "plugin:terminal:default",
            "plugin:terminal:default",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AbortFrame_CallsDispatcherWithConversationId()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = "conv-1",
            Content = new ConnectorContentPayload { Text = "start a turn" },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("abort", new ConnectorAbortPayload
        {
            ConversationId = "conv-1",
        }));
        transport.CompleteIncoming();

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = BuildSession(transport, abortDispatcher: abortDispatcher);
        await session.RunAsync(CancellationToken.None);

        await abortDispatcher.Received(1).AbortAsync(
            Arg.Is<string>(s => s.StartsWith("plugin:")),
            "conv-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AbortFrameNoConversationId_FallsBackToChannelId()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("abort", new ConnectorAbortPayload()));
        transport.CompleteIncoming();

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = BuildSession(transport, abortDispatcher: abortDispatcher);
        await session.RunAsync(CancellationToken.None);

        await abortDispatcher.Received(1).AbortAsync(
            Arg.Is<string>(s => s.StartsWith("plugin:")),
            Arg.Is<string>(s => s.StartsWith("plugin:")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_AbortDispatcherThrows_DoesNotKillLoop()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = "c1",
            Content = new ConnectorContentPayload { Text = "start a turn" },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("abort", new ConnectorAbortPayload
        {
            ConversationId = "c1",
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Content = new ConnectorContentPayload { Text = "still working" },
        }));
        transport.CompleteIncoming();

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("abort failed"));

        var session = BuildSession(transport, abortDispatcher: abortDispatcher);
        var ex = await Record.ExceptionAsync(() => session.RunAsync(CancellationToken.None));

        Assert.Null(ex);
        Assert.Contains(transport.Sent, f => f.Contains("ready"));
    }

    [Fact]
    public async Task RunAsync_PingInterval_SendsPingFrame()
    {
        var fakeTime = new FakeTimeProvider();
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());

        var session = BuildSession(transport, timeProvider: fakeTime);
        var runTask = session.RunAsync(CancellationToken.None);

        await Task.Delay(50);
        fakeTime.Advance(TimeSpan.FromSeconds(ConnectorSession.PingIntervalSeconds));
        await Task.Delay(50);

        Assert.Contains(transport.Sent, f => f.Contains("\"type\":\"ping\""));

        transport.CompleteIncoming();
        await runTask;
    }

    [Fact]
    public async Task RunAsync_HeartbeatTimeout_ClosesWithProtocolViolation()
    {
        var fakeTime = new FakeTimeProvider();
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());

        var session = BuildSession(transport, timeProvider: fakeTime);
        var runTask = session.RunAsync(CancellationToken.None);

        await Task.Delay(50);
        fakeTime.Advance(TimeSpan.FromSeconds(ConnectorSession.HeartbeatTimeoutSeconds));
        await Task.Delay(50);

        await runTask;

        Assert.Contains(transport.Sent, f => f.Contains("protocol_violation") && f.Contains("heartbeat_timeout"));
    }

    // ── 10d. Pong records LastSeenUtc ────────────────────────────────

    [Fact]
    public async Task RunAsync_PongFrame_RecordsLastSeenUtc()
    {
        var clock = new FakeTimeProvider();
        var now = DateTimeOffset.UtcNow;
        clock.SetUtcNow(now);

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("pong", new { }));
        transport.CompleteIncoming();

        var session = BuildSession(transport, timeProvider: clock);
        await session.RunAsync(CancellationToken.None);

        Assert.Equal(now, session.LastSeenUtc);
    }

    // ── 10e. Frame too large in read loop → error + exit ─────────────

    [Fact]
    public async Task RunAsync_FrameTooLargeInLoop_SendsErrorAndExits()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        // Arrange: after handshake, the next receive throws too-large
        transport.Faulted = false;

        var registry3 = Substitute.For<IConnectorRegistry>();
        registry3.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry3.DetachAsync(Arg.Any<PluginChannel>())
            .Returns(_ => ValueTask.CompletedTask);

        var tooLargeTransport = new FaultyAfterHandshakeTransport(HelloFrame(), new ConnectorFrameTooLargeException(100));
        var session = BuildSession(tooLargeTransport, registry: registry3);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(tooLargeTransport.Sent, f => f.Contains("frame_too_large"));
    }

    // ── 11. Teardown detaches and disposes even if no channel ─────────

    [Fact]
    public async Task RunAsync_HandshakeNeverCompleted_TeardownDoesNotThrow()
    {
        var transport = new FakeConnectorTransport();
        transport.CompleteIncoming();

        var registry = Substitute.For<IConnectorRegistry>();
        var session = BuildSession(transport, registry: registry);
        var ex = await Record.ExceptionAsync(() => session.RunAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    // ── 12. Outbound sink writes an outbound frame to the socket ──────

    [Fact]
    public async Task OutboundSink_AfterHandshake_SerialisesOutboundFrameToTransport()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());

        PluginChannel? attached = null;
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                attached = call.Arg<PluginChannel>();
                return ValueTask.FromResult(ConnectorAttachResult.Ok());
            });
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        var run = session.RunAsync(CancellationToken.None);

        // Wait for the handshake to complete before the channel is used.
        await WaitForAsync(() => attached is not null);

        var result = await attached!.SendMessageAsync(new OutboundMessage
        {
            MessageId = "out-1",
            ConversationId = "plugin:terminal:default",
            ChannelId = "plugin:terminal:default",
            Content = new MessageContent { Text = "hello from the agent", IsMarkdown = true },
        });

        transport.CompleteIncoming();
        await run;

        Assert.True(result.Success);
        Assert.Equal("out-1", result.ExternalMessageId);

        var outbound = Assert.Single(transport.Sent, f => f.Contains("\"outbound\""));
        using var doc = JsonDocument.Parse(outbound);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("out-1", payload.GetProperty("messageId").GetString());
        Assert.Equal("hello from the agent", payload.GetProperty("content").GetProperty("text").GetString());
        Assert.True(payload.GetProperty("content").GetProperty("isMarkdown").GetBoolean());
    }

    [Fact]
    public async Task SendMessageAsync_TransportClosed_ReturnsErrorResultRatherThanThrowing()
    {
        var channel = new PluginChannel(
            "terminal",
            "default",
            new ChannelCapabilities(),
            "Terminal",
            NullLogger<PluginChannel>.Instance);

        var result = await channel.SendMessageAsync(new OutboundMessage
        {
            MessageId = "out-2",
            ConversationId = "plugin:terminal:default",
            ChannelId = "plugin:terminal:default",
            Content = new MessageContent { Text = "no sink attached" },
        });

        Assert.False(result.Success);
        Assert.Equal("connector is not attached", result.ErrorMessage);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition was not met within the timeout");
    }

    // ── Phase 4: Replay tests ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_HelloWithoutSinceCursor_ReplayCountZeroAndSourceNotCalled()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.CompleteIncoming();

        var replaySource = Substitute.For<IConnectorReplaySource>();
        var session = BuildSession(transport, replaySource: replaySource);
        await session.RunAsync(CancellationToken.None);

        var readyFrame = Assert.Single(transport.Sent, f => f.Contains("\"ready\""));
        using var doc = JsonDocument.Parse(readyFrame);
        Assert.Equal(0, doc.RootElement.GetProperty("payload").GetProperty("replayCount").GetInt32());

        await replaySource.DidNotReceive().GetMissedMessagesAsync(
            Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_HelloWithUnparseableSinceCursor_ReplayCountZeroAndSourceNotCalled()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(sinceCursor: "not-a-date"));
        transport.CompleteIncoming();

        var replaySource = Substitute.For<IConnectorReplaySource>();
        var session = BuildSession(transport, replaySource: replaySource);
        await session.RunAsync(CancellationToken.None);

        var readyFrame = Assert.Single(transport.Sent, f => f.Contains("\"ready\""));
        using var doc = JsonDocument.Parse(readyFrame);
        Assert.Equal(0, doc.RootElement.GetProperty("payload").GetProperty("replayCount").GetInt32());

        await replaySource.DidNotReceive().GetMissedMessagesAsync(
            Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_HelloWithValidCursorAnd3Missed_ReadySendsCount3ThenOutboundFramesInOrder()
    {
        var cursor = ConnectorCursor.Format(DateTimeOffset.UtcNow.AddMinutes(-10));
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(sinceCursor: cursor));
        transport.CompleteIncoming();

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-9);
        var t1 = DateTimeOffset.UtcNow.AddMinutes(-8);
        var t2 = DateTimeOffset.UtcNow.AddMinutes(-7);
        var missed = new List<ConnectorReplayMessage>
        {
            new() { MessageId = "m1", ConversationId = "c1", Text = "first", Timestamp = t0 },
            new() { MessageId = "m2", ConversationId = "c1", Text = "second", Timestamp = t1 },
            new() { MessageId = "m3", ConversationId = "c1", Text = "third", Timestamp = t2 },
        };

        var replaySource = Substitute.For<IConnectorReplaySource>();
        replaySource.GetMissedMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConnectorReplayMessage>>(missed));

        var session = BuildSession(transport, replaySource: replaySource);
        await session.RunAsync(CancellationToken.None);

        // ready must carry replayCount=3
        var readyFrame = Assert.Single(transport.Sent, f => f.Contains("\"ready\""));
        using var readyDoc = JsonDocument.Parse(readyFrame);
        Assert.Equal(3, readyDoc.RootElement.GetProperty("payload").GetProperty("replayCount").GetInt32());

        // ready must be sent BEFORE the outbound replay frames
        var readyIndex = transport.Sent.IndexOf(readyFrame);
        var outboundFrames = transport.Sent
            .Select((f, i) => (f, i))
            .Where(x => x.f.Contains("\"outbound\""))
            .ToList();

        Assert.Equal(3, outboundFrames.Count);
        Assert.All(outboundFrames, x => Assert.True(x.i > readyIndex));

        // verify order by text
        var texts = outboundFrames.Select(x =>
        {
            using var d = JsonDocument.Parse(x.f);
            return d.RootElement.GetProperty("payload").GetProperty("content").GetProperty("text").GetString();
        }).ToList();

        Assert.Equal(["first", "second", "third"], texts);

        // each replay frame must carry a cursor
        foreach (var (frame, _) in outboundFrames)
        {
            using var d = JsonDocument.Parse(frame);
            var c = d.RootElement.GetProperty("payload").GetProperty("cursor").GetString();
            Assert.False(string.IsNullOrWhiteSpace(c));
            Assert.True(ConnectorCursor.TryParse(c, out _));
        }
    }

    [Fact]
    public async Task RunAsync_ReplaySourceThrows_SessionStillSendsReadyAndContinues()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(sinceCursor: ConnectorCursor.Format(DateTimeOffset.UtcNow.AddMinutes(-5))));
        transport.CompleteIncoming();

        var replaySource = Substitute.For<IConnectorReplaySource>();
        replaySource.GetMissedMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ConnectorReplayMessage>>>(_ => throw new InvalidOperationException("hub gone"));

        var session = BuildSession(transport, replaySource: replaySource);
        var ex = await Record.ExceptionAsync(() => session.RunAsync(CancellationToken.None));
        Assert.Null(ex);

        var readyFrame = Assert.Single(transport.Sent, f => f.Contains("\"ready\""));
        using var doc = JsonDocument.Parse(readyFrame);
        Assert.Equal(0, doc.RootElement.GetProperty("payload").GetProperty("replayCount").GetInt32());
    }

    [Fact]
    public async Task OutboundSink_LiveMessage_CarriesNonEmptyCursorThatRoundTrips()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());

        PluginChannel? attached = null;
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                attached = call.Arg<PluginChannel>();
                return ValueTask.FromResult(ConnectorAttachResult.Ok());
            });
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        var run = session.RunAsync(CancellationToken.None);

        await WaitForAsync(() => attached is not null);

        await attached!.SendMessageAsync(new OutboundMessage
        {
            MessageId = "live-1",
            ConversationId = "plugin:terminal:default",
            ChannelId = "plugin:terminal:default",
            Content = new MessageContent { Text = "live message" },
        });

        transport.CompleteIncoming();
        await run;

        var outbound = Assert.Single(transport.Sent, f => f.Contains("\"outbound\""));
        using var doc = JsonDocument.Parse(outbound);
        var cursorStr = doc.RootElement.GetProperty("payload").GetProperty("cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursorStr));
        Assert.True(ConnectorCursor.TryParse(cursorStr, out _));
    }

    [Fact]
    public async Task OutboundSink_MessageWithAgentTimestamp_UsesItAsCursorRatherThanTheClock()
    {
        // The cursor a connector sends back as sinceCursor is compared against timestamps in
        // the agent's history store, so it must BE the agent's timestamp. Using the Bridge's
        // send-time clock would silently skip anything persisted between the store write and
        // the dispatch.
        var agentTimestamp = new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.Zero);

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());

        PluginChannel? attached = null;
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                attached = call.Arg<PluginChannel>();
                return ValueTask.FromResult(ConnectorAttachResult.Ok());
            });
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        var run = session.RunAsync(CancellationToken.None);

        await WaitForAsync(() => attached is not null);

        await attached!.SendMessageAsync(new OutboundMessage
        {
            MessageId = "live-2",
            ConversationId = "plugin:terminal:default",
            ChannelId = "plugin:terminal:default",
            Content = new MessageContent { Text = "stored message" },
            Timestamp = agentTimestamp,
        });

        transport.CompleteIncoming();
        await run;

        var frame = Assert.Single(transport.Sent, f => f.Contains("\"outbound\""));
        using var parsed = JsonDocument.Parse(frame);
        var cursor = parsed.RootElement.GetProperty("payload").GetProperty("cursor").GetString();

        Assert.True(ConnectorCursor.TryParse(cursor, out var roundTripped));
        Assert.Equal(agentTimestamp, roundTripped);
    }

    [Fact]
    public async Task RunAsync_EndToEnd_ReattachWithCursorReplaysExactlyMissedMessages()
    {
        // Simulate: connect → receive outbound → disconnect → reconnect with cursor → assert replay
        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t1 = new DateTimeOffset(2026, 1, 1, 12, 1, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 1, 12, 2, 0, TimeSpan.Zero);

        // The last outbound frame the connector saw had timestamp t0.
        var cursorFromLastSeen = ConnectorCursor.Format(t0);

        // On re-attach, messages t1 and t2 are "missed".
        var missed = new List<ConnectorReplayMessage>
        {
            new() { MessageId = "r1", ConversationId = "ch", Text = "msg at t1", Timestamp = t1 },
            new() { MessageId = "r2", ConversationId = "ch", Text = "msg at t2", Timestamp = t2 },
        };

        var replaySource = Substitute.For<IConnectorReplaySource>();
        replaySource.GetMissedMessagesAsync(
                Arg.Any<string>(),
                Arg.Is<DateTimeOffset>(d => d == t0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConnectorReplayMessage>>(missed));

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame(sinceCursor: cursorFromLastSeen));
        transport.CompleteIncoming();

        var session = BuildSession(transport, replaySource: replaySource);
        await session.RunAsync(CancellationToken.None);

        var outboundFrames = transport.Sent.Where(f => f.Contains("\"outbound\"")).ToList();
        Assert.Equal(2, outboundFrames.Count);

        var texts = outboundFrames.Select(f =>
        {
            using var d = JsonDocument.Parse(f);
            return d.RootElement.GetProperty("payload").GetProperty("content").GetProperty("text").GetString();
        }).ToList();

        Assert.Equal("msg at t1", texts[0]);
        Assert.Equal("msg at t2", texts[1]);
    }

    // ── Phase 6: Rate limiting ────────────────────────────────────────

    private static ConnectorSettingsConfig SettingsWithLimit(int maxPerMinute) => new()
    {
        Enabled = true,
        MaxConnectors = 16,
        Limits = new Cortex.Contained.Contracts.Config.ConnectorLimitsConfig
        {
            MaxMessagesPerMinute = maxPerMinute,
        },
    };

    [Fact]
    public async Task RunAsync_InboundAtLimit_AllowsExactlyLimitMessages()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "msg 1" } }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "msg 2" } }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "msg 3 - over limit" } }));
        transport.CompleteIncoming();

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry, settings: SettingsWithLimit(2));
        await session.RunAsync(CancellationToken.None);

        Assert.Equal(2, received.Count);
        Assert.Contains(transport.Sent, f => f.Contains("rate_limited"));
    }

    [Fact]
    public async Task RunAsync_RateLimitedInbound_SessionSurvivesAndContinues()
    {
        // After a rate_limited rejection the session must NOT close.
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "msg 1" } }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "over limit" } }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "also over limit" } }));
        transport.CompleteIncoming();

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry, settings: SettingsWithLimit(1));
        await session.RunAsync(CancellationToken.None);

        Assert.Single(received); // only the first message
        var rateLimitedCount = transport.Sent.Count(f => f.Contains("rate_limited"));
        Assert.Equal(2, rateLimitedCount); // two rejections
    }

    [Fact]
    public async Task RunAsync_PongNotRateLimited_ProcessedWithoutRateLimitedError()
    {
        // Exhaust the limit with inbound, then send pong — pong must never be rate limited.
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "exhaust" } }));
        transport.QueueIncoming(ConnectorFrame.Serialize("pong", new { }));
        transport.CompleteIncoming();

        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry, settings: SettingsWithLimit(1));
        await session.RunAsync(CancellationToken.None);

        // pong must not produce a rate_limited frame after the limit is hit
        // (there may be one from the next inbound if we add one, but not from pong itself)
        Assert.DoesNotContain(transport.Sent, f =>
            f.Contains("rate_limited") && transport.Sent.IndexOf(f) > transport.Sent.FindIndex(x => x.Contains("pong")));
    }

    [Fact]
    public async Task RunAsync_AbortNotRateLimited_DispatcherCalledEvenWhenLimitExhausted()
    {
        // Exhaust the rate limit, then send abort for own channel — abort must not be rate limited.
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        // First inbound establishes conv ownership and exhausts the 1-msg limit.
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = "my-conv",
            Content = new ConnectorContentPayload { Text = "sets up ownership" },
        }));
        // Abort for a conversation this session owns — should NOT be rate limited.
        transport.QueueIncoming(ConnectorFrame.Serialize("abort", new ConnectorAbortPayload
        {
            ConversationId = "my-conv",
        }));
        transport.CompleteIncoming();

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = BuildSession(transport, abortDispatcher: abortDispatcher, settings: SettingsWithLimit(1));
        await session.RunAsync(CancellationToken.None);

        await abortDispatcher.Received(1).AbortAsync(Arg.Any<string>(), "my-conv", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_RateLimitAfterWindowSlides_AllowsNewMessages()
    {
        var fakeTime = new FakeTimeProvider();
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry, settings: SettingsWithLimit(1), timeProvider: fakeTime);
        var runTask = session.RunAsync(CancellationToken.None);

        // Wait for session to attach channel.
        await WaitForAsync(() => session.Channel is not null);

        // First message — within limit.
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "first" } }));
        await WaitForAsync(() => received.Count == 1);

        // Second message — over limit, should be rejected.
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "second — rate limited" } }));
        await WaitForAsync(() => transport.Sent.Any(f => f.Contains("rate_limited")));

        // Advance time past the 60-second window.
        fakeTime.Advance(TimeSpan.FromSeconds(61));

        // Third message — window has slid, should be allowed again.
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload { Content = new ConnectorContentPayload { Text = "third — should pass" } }));
        await WaitForAsync(() => received.Count == 2);

        transport.CompleteIncoming();
        await runTask;

        Assert.Equal(2, received.Count); // first and third
    }

    // ── Phase 6: maxMessageLength enforcement ─────────────────────────

    [Fact]
    public async Task RunAsync_InboundOverNegotiatedMaxLength_SendsMessageTooLong()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Capabilities = new ConnectorCapabilitiesPayload { MaxMessageLength = 10 },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Content = new ConnectorContentPayload { Text = "12345678901" }, // 11 chars — over limit
        }));
        transport.CompleteIncoming();

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.Empty(received);
        Assert.Contains(transport.Sent, f => f.Contains("message_too_long"));
    }

    [Fact]
    public async Task RunAsync_InboundExactlyAtNegotiatedMaxLength_IsDelivered()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Capabilities = new ConnectorCapabilitiesPayload { MaxMessageLength = 10 },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Content = new ConnectorContentPayload { Text = "1234567890" }, // exactly 10 chars
        }));
        transport.CompleteIncoming();

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.Single(received);
        Assert.DoesNotContain(transport.Sent, f => f.Contains("message_too_long"));
    }

    // ── Phase 6: Handshake timeout ────────────────────────────────────

    [Fact]
    public async Task RunAsync_NoHelloWithinTimeout_SendsProtocolViolationHandshakeTimeout()
    {
        var fakeTime = new FakeTimeProvider();
        var transport = new FakeConnectorTransport(); // no frames queued

        var session = BuildSession(transport, timeProvider: fakeTime);
        var runTask = session.RunAsync(CancellationToken.None);

        // Give the session a moment to reach ReceiveAsync.
        await Task.Delay(20);

        // Advance past the handshake timeout.
        fakeTime.Advance(TimeSpan.FromSeconds(ConnectorSession.HandshakeTimeoutSeconds + 1));

        await runTask;

        Assert.Contains(transport.Sent, f => f.Contains("protocol_violation"));
        Assert.Contains(transport.Sent, f => f.Contains("handshake_timeout"));
    }

    // ── Phase 6: Unbounded input — id length rejection ────────────────

    [Fact]
    public async Task RunAsync_MessageIdTooLong_SendsInvalidPayloadAndContinues()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            MessageId = new string('x', ConnectorSession.MaxIdLength + 1),
            Content = new ConnectorContentPayload { Text = "hello" },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            MessageId = "ok-id",
            Content = new ConnectorContentPayload { Text = "still works" },
        }));
        transport.CompleteIncoming();

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        Assert.Single(received); // second message got through
        Assert.Contains(transport.Sent, f => f.Contains("invalid_payload"));
    }

    [Fact]
    public async Task RunAsync_ConversationIdTooLong_SendsInvalidPayload()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = new string('c', ConnectorSession.MaxIdLength + 1),
            Content = new ConnectorContentPayload { Text = "hello" },
        }));
        transport.CompleteIncoming();

        var session = BuildSession(transport);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("invalid_payload"));
    }

    [Fact]
    public async Task RunAsync_SenderIdTooLong_SendsInvalidPayload()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Sender = new ConnectorSenderPayload { Id = new string('s', ConnectorSession.MaxIdLength + 1) },
            Content = new ConnectorContentPayload { Text = "hello" },
        }));
        transport.CompleteIncoming();

        var session = BuildSession(transport);
        await session.RunAsync(CancellationToken.None);

        Assert.Contains(transport.Sent, f => f.Contains("invalid_payload"));
    }

    [Fact]
    public async Task RunAsync_SenderDisplayNameTooLong_TruncatesAndDelivers()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(HelloFrame());
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            Sender = new ConnectorSenderPayload
            {
                Id = "user1",
                DisplayName = new string('d', ConnectorSession.MaxDisplayNameLength + 10),
            },
            Content = new ConnectorContentPayload { Text = "hello" },
        }));
        transport.CompleteIncoming();

        var received = new List<InboundMessage>();
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch =>
        {
            ch.MessageReceived += msg => { received.Add(msg); return Task.CompletedTask; };
        }), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = BuildSession(transport, registry: registry);
        await session.RunAsync(CancellationToken.None);

        // Message is delivered (not rejected) with a truncated display name.
        Assert.Single(received);
        Assert.Equal(ConnectorSession.MaxDisplayNameLength, received[0].Sender.DisplayName?.Length);
        Assert.DoesNotContain(transport.Sent, f => f.Contains("invalid_payload"));
    }
}

/// <summary>Transport that succeeds the hello then faults on the next receive.</summary>
internal sealed class FaultyAfterHandshakeTransport : IConnectorTransport
{
    private readonly string helloJson;
    private readonly Exception fault;
    private int receiveCount;

    public List<string> Sent { get; } = new();

    public bool IsOpen => true;
    public string RemoteEndpoint => "127.0.0.1:0";

    public FaultyAfterHandshakeTransport(string helloJson, Exception fault)
    {
        this.helloJson = helloJson;
        this.fault = fault;
    }

    public Task<string?> ReceiveAsync(CancellationToken ct)
    {
        this.receiveCount++;
        if (this.receiveCount == 1) return Task.FromResult<string?>(this.helloJson);
        throw this.fault;
    }

    public Task SendAsync(string json, CancellationToken ct) { this.Sent.Add(json); return Task.CompletedTask; }
    public Task CloseAsync(string reason, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

