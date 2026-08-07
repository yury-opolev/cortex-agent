using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public class PluginChannelTests
{
    private static PluginChannel CreateChannel(
        string key = "terminal",
        string instanceId = "default",
        string displayName = "Terminal")
    {
        return new PluginChannel(
            key,
            instanceId,
            new ChannelCapabilities(),
            displayName,
            NullLogger<PluginChannel>.Instance);
    }

    [Fact]
    public void Constructor_SetsProperties()
    {
        var ch = CreateChannel("mykey", "inst1", "My Connector");

        Assert.Equal("plugin:mykey:inst1", ch.ChannelId);
        Assert.Equal("mykey", ch.PluginKey);
        Assert.Equal("inst1", ch.InstanceId);
        Assert.Equal("My Connector", ch.DisplayName);
        Assert.Equal(ChannelType.Plugin, ch.Type);
        Assert.Equal(ChannelStatus.Disconnected, ch.Status);
    }

    [Fact]
    public async Task ConnectAsync_SetsStatusConnected_FiresStatusChanged()
    {
        var ch = CreateChannel();
        ChannelStatusChange? change = null;
        ch.StatusChanged += c => { change = c; return Task.CompletedTask; };

        await ch.ConnectAsync();

        Assert.Equal(ChannelStatus.Connected, ch.Status);
        Assert.NotNull(change);
        Assert.Equal(ChannelStatus.Disconnected, change.PreviousStatus);
        Assert.Equal(ChannelStatus.Connected, change.CurrentStatus);
    }

    [Fact]
    public async Task ConnectAsync_Idempotent_DoesNotFireStatusChangedTwice()
    {
        var ch = CreateChannel();
        var count = 0;
        ch.StatusChanged += _ => { count++; return Task.CompletedTask; };

        await ch.ConnectAsync();
        await ch.ConnectAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task DisconnectAsync_SetsStatusDisconnected_FiresStatusChanged()
    {
        var ch = CreateChannel();
        await ch.ConnectAsync();

        ChannelStatusChange? change = null;
        ch.StatusChanged += c => { change = c; return Task.CompletedTask; };

        await ch.DisconnectAsync();

        Assert.Equal(ChannelStatus.Disconnected, ch.Status);
        Assert.NotNull(change);
        Assert.Equal(ChannelStatus.Connected, change.PreviousStatus);
        Assert.Equal(ChannelStatus.Disconnected, change.CurrentStatus);
    }

    [Fact]
    public async Task DisconnectAsync_Idempotent_DoesNotFireStatusChangedTwice()
    {
        var ch = CreateChannel();
        await ch.ConnectAsync();

        var count = 0;
        ch.StatusChanged += _ => { count++; return Task.CompletedTask; };

        await ch.DisconnectAsync();
        await ch.DisconnectAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SendMessageAsync_NoSink_ReturnsError()
    {
        var ch = CreateChannel();
        var msg = new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = ch.ChannelId,
            Content = new MessageContent { Text = "hi" },
        };

        var result = await ch.SendMessageAsync(msg);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task SendMessageAsync_WithSink_DelegatesToSink()
    {
        var ch = CreateChannel();
        OutboundMessage? captured = null;
        ch.OutboundSink = (m, _) =>
        {
            captured = m;
            return Task.FromResult(SendResult.Ok("ext-1"));
        };

        var msg = new OutboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = ch.ChannelId,
            Content = new MessageContent { Text = "hi" },
        };

        var result = await ch.SendMessageAsync(msg);

        Assert.True(result.Success);
        Assert.Same(msg, captured);
    }

    [Fact]
    public async Task ReceiveInboundAsync_FiresMessageReceived()
    {
        var ch = CreateChannel();
        InboundMessage? received = null;
        ch.MessageReceived += m => { received = m; return Task.CompletedTask; };

        var msg = new InboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = ch.ChannelId,
            ChannelType = ChannelType.Plugin,
            Sender = new SenderInfo { Id = "u1", DisplayName = "User" },
            Content = new MessageContent { Text = "hello" },
            Timestamp = DateTimeOffset.UtcNow,
        };

        await ch.ReceiveInboundAsync(msg);

        Assert.Same(msg, received);
    }

    [Fact]
    public async Task ReceiveInboundAsync_NoSubscribers_DoesNotThrow()
    {
        var ch = CreateChannel();
        var msg = new InboundMessage
        {
            MessageId = "m1",
            ConversationId = "c1",
            ChannelId = ch.ChannelId,
            ChannelType = ChannelType.Plugin,
            Sender = new SenderInfo { Id = "u1", DisplayName = "User" },
            Content = new MessageContent { Text = "hello" },
            Timestamp = DateTimeOffset.UtcNow,
        };

        var ex = await Record.ExceptionAsync(() => ch.ReceiveInboundAsync(msg));

        Assert.Null(ex);
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsIfConnected()
    {
        var ch = CreateChannel();
        await ch.ConnectAsync();

        await ch.DisposeAsync();

        Assert.Equal(ChannelStatus.Disconnected, ch.Status);
    }
}
