#pragma warning disable CA2012
using System.Text.Json;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Connectors.Protocol;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class StreamingPluginChannelTests
{
    private static StreamingPluginChannel CreateChannel()
        => new("terminal", "default", new ChannelCapabilities { SupportsStreaming = true }, "Terminal", NullLoggerFactory.Instance);

    [Fact]
    public async Task SendTypingIndicatorAsync_SinkSet_EmitsTypingFrame()
    {
        var channel = CreateChannel();
        var captured = new List<string>();
        channel.FrameSink = (json, _) => { captured.Add(json); return Task.CompletedTask; };

        await channel.SendTypingIndicatorAsync("conv1");

        Assert.Single(captured);
        var frame = JsonDocument.Parse(captured[0]).RootElement;
        Assert.Equal("typing", frame.GetProperty("type").GetString());
        Assert.Equal("conv1", frame.GetProperty("payload").GetProperty("conversationId").GetString());
    }

    [Fact]
    public async Task SendTypingIndicatorAsync_NullSink_IsNoOp()
    {
        var channel = CreateChannel();
        channel.FrameSink = null;

        var ex = await Record.ExceptionAsync(() => channel.SendTypingIndicatorAsync("conv1"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SendTypingIndicatorAsync_ThrowingSink_DoesNotPropagate()
    {
        var channel = CreateChannel();
        channel.FrameSink = (_, _) => throw new InvalidOperationException("boom");

        var ex = await Record.ExceptionAsync(() => channel.SendTypingIndicatorAsync("conv1"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SendStreamingUpdateAsync_SinkSet_EmitsStreamFrame()
    {
        var channel = CreateChannel();
        var captured = new List<string>();
        channel.FrameSink = (json, _) => { captured.Add(json); return Task.CompletedTask; };

        await channel.SendStreamingUpdateAsync("conv1", "Hello ");

        Assert.Single(captured);
        var frame = JsonDocument.Parse(captured[0]).RootElement;
        Assert.Equal("stream", frame.GetProperty("type").GetString());
        Assert.Equal("conv1", frame.GetProperty("payload").GetProperty("conversationId").GetString());
        Assert.Equal("Hello ", frame.GetProperty("payload").GetProperty("delta").GetString());
    }

    [Fact]
    public async Task SendStreamingUpdateAsync_NullSink_IsNoOp()
    {
        var channel = CreateChannel();
        channel.FrameSink = null;

        var ex = await Record.ExceptionAsync(() => channel.SendStreamingUpdateAsync("conv1", "hi"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task SendStreamingUpdateAsync_ThrowingSink_DoesNotPropagate()
    {
        var channel = CreateChannel();
        channel.FrameSink = (_, _) => throw new InvalidOperationException("boom");

        var ex = await Record.ExceptionAsync(() => channel.SendStreamingUpdateAsync("conv1", "hi"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task FinalizeStreamingAsync_SinkSet_DelegatesToOutboundSink()
    {
        var channel = CreateChannel();
        OutboundMessage? captured = null;
        channel.OutboundSink = (m, _) => { captured = m; return Task.FromResult(SendResult.Ok(m.MessageId)); };

        var msg = new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = channel.ChannelId,
            Content = new MessageContent { Text = "final" },
        };

        await channel.FinalizeStreamingAsync("c1", msg);

        Assert.NotNull(captured);
        Assert.Equal("m1", captured!.MessageId);
    }

    [Fact]
    public async Task FinalizeStreamingAsync_NoSink_DoesNotThrow()
    {
        var channel = CreateChannel();
        var msg = new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = channel.ChannelId,
            Content = new MessageContent { Text = "final" },
        };

        var ex = await Record.ExceptionAsync(() => channel.FinalizeStreamingAsync("c1", msg));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Session_StreamingChannel_StreamThenFinalizeProducesFramesInOrder()
    {
        var transport = new FakeConnectorTransport();
        transport.QueueIncoming(ConnectorFrame.Serialize("hello", new ConnectorHelloPayload
        {
            Key = "terminal",
            Capabilities = new ConnectorCapabilitiesPayload { Streaming = true },
        }));
        transport.CompleteIncoming();

        var registry = Substitute.For<IConnectorRegistry>();
        registry.TryAttachAsync(Arg.Any<PluginChannel>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAttachResult.Ok()));
        registry.DetachAsync(Arg.Any<PluginChannel>()).Returns(ValueTask.CompletedTask);

        var abortDispatcher = Substitute.For<IConnectorAbortDispatcher>();
        abortDispatcher.AbortAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var session = new ConnectorSession(
            transport,
            CreateAuthenticator(),
            new ConnectorSettingsConfig { Enabled = true, MaxConnectors = 16 },
            registry,
            NullLoggerFactory.Instance,
            TimeProvider.System,
            abortDispatcher);

        await session.RunAsync(CancellationToken.None);

        var channel = Assert.IsType<StreamingPluginChannel>(session.Channel);

        await channel.SendStreamingUpdateAsync("c1", "hello ");
        await channel.FinalizeStreamingAsync("c1", new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = channel.ChannelId,
            Content = new MessageContent { Text = "hello world" },
        });

        var frameTypes = transport.Sent
            .Select(json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString())
            .ToList();

        var streamIdx = frameTypes.IndexOf("stream");
        var outboundIdx = frameTypes.IndexOf("outbound");

        Assert.True(streamIdx >= 0, "stream frame expected");
        Assert.True(outboundIdx >= 0, "outbound frame expected");
        Assert.True(streamIdx < outboundIdx, "stream must come before outbound");
    }

    private static IConnectorAuthenticator CreateAuthenticator()
    {
        var authenticator = Substitute.For<IConnectorAuthenticator>();
        authenticator.AuthenticateAsync(Arg.Any<ConnectorAuthRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult(ConnectorAuthResult.Approved("tok")));
        return authenticator;
    }
}
