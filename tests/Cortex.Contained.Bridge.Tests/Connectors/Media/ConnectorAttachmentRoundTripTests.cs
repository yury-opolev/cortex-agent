// NSubstitute .Returns() consumes the ValueTask; the analyzer cannot see that.
#pragma warning disable CA2012
using System.Text.Json;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Media;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Bridge.Connectors.Replay;
using Cortex.Contained.Bridge.Connectors.Security;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Connectors.Media;

/// <summary>
/// End-to-end tests for the handle carrying mode: the REST upload/fetch surface and the WebSocket
/// session sharing one attachment store, exactly as they are composed in <c>Program.cs</c>.
/// The unit tests cover each component in isolation; these prove the seams line up, which is the
/// part that a mocked test cannot demonstrate.
/// </summary>
public sealed class ConnectorAttachmentRoundTripTests
{
    private const string ChannelId = "plugin:terminal:default";
    private const string Token = "connector-token-value";
    private const int OneMebibyte = 1_048_576;

    private sealed class Harness
    {
        public required ConnectorAttachmentService Service { get; init; }

        public required ConnectorAttachmentStore Store { get; init; }

        public required ConnectorSettingsConfig Settings { get; init; }

        public required FakeTimeProvider Time { get; init; }
    }

    private static Harness Build(Action<ConnectorMediaConfig>? configure = null)
    {
        var settings = new ConnectorSettingsConfig { Enabled = true };
        configure?.Invoke(settings.Media);

        var policy = ConnectorMediaPolicy.From(settings.Media, settings.Limits.MaxFrameBytes);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-09T12:00:00Z", null));

        var secretStore = new FakeConnectorSecretStore();
        var tokenStore = new ConnectorTokenStore(secretStore, NullLogger<ConnectorTokenStore>.Instance);
        tokenStore.Save(new ConnectorRecord
        {
            ChannelId = ChannelId,
            Key = "terminal",
            InstanceId = "default",
            DisplayName = "Terminal",
            Token = Token,
            PairedAt = DateTimeOffset.UnixEpoch,
        });

        var store = new ConnectorAttachmentStore(policy, time, NullLogger<ConnectorAttachmentStore>.Instance);

        return new Harness
        {
            Service = new ConnectorAttachmentService(
                tokenStore,
                store,
                new ConnectorUploadRateLimiter(policy.MaxUploadsPerMinute, time),
                policy,
                time,
                NullLogger<ConnectorAttachmentService>.Instance),
            Store = store,
            Settings = settings,
            Time = time,
        };
    }

    private static byte[] LargePng(int totalBytes)
    {
        var data = new byte[totalBytes];
        ImageContentSnifferTests.Png.AsSpan(0, 8).CopyTo(data);

        // Fill the tail so the payload is not trivially compressible and the size is meaningful.
        for (var i = 8; i < totalBytes; i++)
        {
            data[i] = (byte)(i % 251);
        }

        return data;
    }

    // ── Connector -> agent ───────────────────────────────────────────

    [Fact]
    public async Task ConnectorUploadsThenReferencesHandle_AgentReceivesTheBytes()
    {
        var h = Build();
        var original = LargePng(512 * 1024);

        // 1. The connector uploads out of band, over the authenticated REST endpoint.
        var upload = h.Service.Upload($"Bearer {Token}", original, "image/png", "big.png");
        Assert.True(upload.Success);

        // 2. It then references the handle from an ordinary inbound frame, which stays tiny.
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Token = Token,
            Capabilities = new ConnectorCapabilitiesPayload { Media = true },
        }));

        var inboundFrame = ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = "conv-1",
            Content = new ConnectorContentPayload
            {
                Text = "what is in this?",
                Attachments = [new ConnectorAttachmentPayload { MimeType = "image/png", Handle = upload.Handle }],
            },
        });
        transport.QueueIncoming(inboundFrame);
        transport.CompleteIncoming();

        Assert.True(
            inboundFrame.Length < 4096,
            "the whole point of a handle is that the frame stays small regardless of payload size");

        List<InboundMessage> received = [];
        var session = BuildSession(transport, h, received);
        await session.RunAsync(CancellationToken.None);

        // 3. The agent receives the real bytes.
        var message = Assert.Single(received);
        var attachment = Assert.Single(message.Content.Attachments!);
        Assert.Equal(original, attachment.Data);
        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal(original.LongLength, attachment.SizeBytes);
    }

    [Fact]
    public async Task ConnectorReferencesAlreadyConsumedHandle_MessageIsRejected()
    {
        var h = Build();
        var upload = h.Service.Upload($"Bearer {Token}", LargePng(1024), "image/png");

        // Something already consumed it — a handle is single-use.
        h.Store.Consume(upload.Handle!, ChannelId);

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Token = Token,
            Capabilities = new ConnectorCapabilitiesPayload { Media = true },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = "conv-1",
            Content = new ConnectorContentPayload
            {
                Text = "hi",
                Attachments = [new ConnectorAttachmentPayload { MimeType = "image/png", Handle = upload.Handle }],
            },
        }));
        transport.CompleteIncoming();

        List<InboundMessage> received = [];
        var session = BuildSession(transport, h, received);
        await session.RunAsync(CancellationToken.None);

        Assert.Empty(received);
        Assert.Contains(transport.Sent, f => f.Contains("attachment_not_found"));
    }

    [Fact]
    public async Task ConnectorReferencesExpiredHandle_MessageIsRejected()
    {
        var h = Build(c => c.HandleTtl = TimeSpan.FromMinutes(10));
        var upload = h.Service.Upload($"Bearer {Token}", LargePng(1024), "image/png");

        h.Time.Advance(TimeSpan.FromMinutes(11));

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Token = Token,
            Capabilities = new ConnectorCapabilitiesPayload { Media = true },
        }));
        transport.QueueIncoming(ConnectorFrame.Serialize("inbound", new ConnectorInboundPayload
        {
            ConversationId = "conv-1",
            Content = new ConnectorContentPayload
            {
                Text = "hi",
                Attachments = [new ConnectorAttachmentPayload { MimeType = "image/png", Handle = upload.Handle }],
            },
        }));
        transport.CompleteIncoming();

        List<InboundMessage> received = [];
        var session = BuildSession(transport, h, received);
        await session.RunAsync(CancellationToken.None);

        Assert.Empty(received);
        Assert.Contains(transport.Sent, f => f.Contains("attachment_not_found"));
    }

    // ── Agent -> connector ───────────────────────────────────────────

    [Fact]
    public async Task AgentSendsLargeAttachment_ConnectorGetsAHandleItCanFetch()
    {
        var h = Build();
        var original = LargePng(512 * 1024);

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Token = Token,
            Capabilities = new ConnectorCapabilitiesPayload { Media = true },
        }));

        PluginChannel? channel = null;
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch => channel = ch), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = NewSession(transport, h, registry, []);
        var run = session.RunAsync(CancellationToken.None);

        while (channel is null)
        {
            await Task.Delay(5);
        }

        await channel.SendMessageAsync(new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "conv-1",
            ChannelId = channel.ChannelId,
            Content = new MessageContent
            {
                Text = "here is the chart",
                Attachments =
                [
                    new MediaAttachment { MimeType = "image/png", FileName = "chart.png", Data = original },
                ],
            },
        });

        transport.CompleteIncoming();
        await run;

        // 1. The frame carries a handle, not half a megabyte of base64, and stays small.
        var frame = Assert.Single(transport.Sent, f => f.Contains("\"outbound\""));
        Assert.True(frame.Length < 4096, "a handle-carried attachment must not inflate the frame");

        using var doc = JsonDocument.Parse(frame);
        var attachment = doc.RootElement.GetProperty("payload").GetProperty("content")
            .GetProperty("attachments")[0];

        var handle = attachment.GetProperty("handle").GetString();
        Assert.NotNull(handle);
        Assert.False(attachment.TryGetProperty("data", out _));
        Assert.False(attachment.TryGetProperty("url", out _));

        // 2. The connector fetches it over the authenticated REST endpoint and gets the real bytes.
        var fetch = h.Service.Fetch($"Bearer {Token}", handle);
        Assert.True(fetch.Success);
        Assert.Equal(original, fetch.Content!.Data);
        Assert.Equal("chart.png", fetch.Content.FileName);
    }

    [Fact]
    public async Task AgentSendsLargeAttachment_AnotherConnectorCannotFetchTheHandle()
    {
        var h = Build();

        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Token = Token,
            Capabilities = new ConnectorCapabilitiesPayload { Media = true },
        }));

        PluginChannel? channel = null;
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Do<PluginChannel>(ch => channel = ch), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        var session = NewSession(transport, h, registry, []);
        var run = session.RunAsync(CancellationToken.None);

        while (channel is null)
        {
            await Task.Delay(5);
        }

        await channel.SendMessageAsync(new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "conv-1",
            ChannelId = channel.ChannelId,
            Content = new MessageContent
            {
                Attachments = [new MediaAttachment { MimeType = "image/png", Data = LargePng(512 * 1024) }],
            },
        });

        transport.CompleteIncoming();
        await run;

        var frame = Assert.Single(transport.Sent, f => f.Contains("\"outbound\""));
        using var doc = JsonDocument.Parse(frame);
        var handle = doc.RootElement.GetProperty("payload").GetProperty("content")
            .GetProperty("attachments")[0].GetProperty("handle").GetString();

        // The handle is scoped to the channel it was issued to; a different token cannot spend it.
        Assert.Null(h.Store.Consume(handle!, "plugin:other:default"));

        // And it is still there for its rightful owner.
        Assert.NotNull(h.Store.Consume(handle!, ChannelId));
    }

    private static ConnectorSession BuildSession(
        FakeConnectorTransport transport,
        Harness harness,
        List<InboundMessage> received)
    {
        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(
                Arg.Do<PluginChannel>(ch => ch.MessageReceived += m =>
                {
                    received.Add(m);
                    return Task.CompletedTask;
                }),
                Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(_ => ValueTask.CompletedTask);

        return NewSession(transport, harness, registry, received);
    }

    private static ConnectorSession NewSession(
        FakeConnectorTransport transport,
        Harness harness,
        IConnectorRegistry registry,
        List<InboundMessage> received)
    {
        _ = received;

        var authenticator = Substitute.For<IConnectorAuthenticator>();
        authenticator.AuthenticateAsync(Arg.Any<ConnectorAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAuthResult.Approved(null)));

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var replaySource = Substitute.For<IConnectorReplaySource>();
        replaySource.GetMissedMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConnectorReplayMessage>>([]));

        return new ConnectorSession(
            transport,
            authenticator,
            harness.Settings,
            registry,
            NullLoggerFactory.Instance,
            harness.Time,
            abortDispatcher,
            replaySource,
            harness.Store,
            harness.Store);
    }
}
