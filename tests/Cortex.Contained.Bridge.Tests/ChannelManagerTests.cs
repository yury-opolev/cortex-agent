using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public class ChannelManagerTests : IAsyncDisposable
{
    private readonly ChannelManager _manager;

    public ChannelManagerTests()
    {
        _manager = new ChannelManager(NullLogger<ChannelManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void RegisterChannel_AddsChannel()
    {
        var channel = CreateMockChannel("ch-1");

        _manager.RegisterChannel(channel);

        var found = _manager.TryGetChannel("ch-1", out var result);
        Assert.True(found);
        Assert.Same(channel, result);
    }

    [Fact]
    public void RegisterChannel_MultipleChannels()
    {
        var ch1 = CreateMockChannel("ch-1");
        var ch2 = CreateMockChannel("ch-2");
        var ch3 = CreateMockChannel("ch-3");

        _manager.RegisterChannel(ch1);
        _manager.RegisterChannel(ch2);
        _manager.RegisterChannel(ch3);

        Assert.True(_manager.TryGetChannel("ch-1", out _));
        Assert.True(_manager.TryGetChannel("ch-2", out _));
        Assert.True(_manager.TryGetChannel("ch-3", out _));

        var all = _manager.GetAllChannels();
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void TryGetChannel_NonExistent_ReturnsFalse()
    {
        var channel = CreateMockChannel("ch-1");
        _manager.RegisterChannel(channel);

        var found = _manager.TryGetChannel("does-not-exist", out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void GetChannelsByType_FiltersCorrectly()
    {
        var webChat1 = CreateMockChannel("web-1", ChannelType.WebChat);
        var webChat2 = CreateMockChannel("web-2", ChannelType.WebChat);
        var discord = CreateMockChannel("discord-1", ChannelType.Discord);
        var teams = CreateMockChannel("teams-1", ChannelType.Teams);

        _manager.RegisterChannel(webChat1);
        _manager.RegisterChannel(webChat2);
        _manager.RegisterChannel(discord);
        _manager.RegisterChannel(teams);

        var webChats = _manager.GetChannelsByType(ChannelType.WebChat);
        var discords = _manager.GetChannelsByType(ChannelType.Discord);
        var teamsList = _manager.GetChannelsByType(ChannelType.Teams);
        var telegrams = _manager.GetChannelsByType(ChannelType.Telegram);

        Assert.Equal(2, webChats.Count);
        Assert.Single(discords);
        Assert.Single(teamsList);
        Assert.Empty(telegrams);
    }

    [Fact]
    public void GetAllChannels_ReturnsAll()
    {
        var ch1 = CreateMockChannel("ch-1");
        var ch2 = CreateMockChannel("ch-2");

        _manager.RegisterChannel(ch1);
        _manager.RegisterChannel(ch2);

        var all = _manager.GetAllChannels();

        Assert.Equal(2, all.Count);
        Assert.Contains(ch1, all);
        Assert.Contains(ch2, all);
    }

    [Fact]
    public void GetAllChannels_Empty_ReturnsEmpty()
    {
        var all = _manager.GetAllChannels();

        Assert.Empty(all);
    }

    [Fact]
    public async Task ConnectAllAsync_ConnectsAllChannels()
    {
        var ch1 = CreateMockChannel("ch-1");
        var ch2 = CreateMockChannel("ch-2");

        _manager.RegisterChannel(ch1);
        _manager.RegisterChannel(ch2);

        await _manager.ConnectAllAsync(CancellationToken.None);

        await ch1.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await ch2.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConnectAllAsync_ChannelThrows_ContinuesWithOthers()
    {
        var ch1 = CreateMockChannel("ch-1");
        var ch2 = CreateMockChannel("ch-2");
        var ch3 = CreateMockChannel("ch-3");

        ch2.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Connection failed")));

        _manager.RegisterChannel(ch1);
        _manager.RegisterChannel(ch2);
        _manager.RegisterChannel(ch3);

        // Should not throw even though ch2 fails
        await _manager.ConnectAllAsync(CancellationToken.None);

        await ch1.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await ch2.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
        await ch3.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisconnectAllAsync_DisconnectsAll()
    {
        var ch1 = CreateMockChannel("ch-1");
        var ch2 = CreateMockChannel("ch-2");

        _manager.RegisterChannel(ch1);
        _manager.RegisterChannel(ch2);

        await _manager.DisconnectAllAsync();

        await ch1.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await ch2.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsAndDisposes()
    {
        var ch1 = CreateMockChannel("ch-1");
        var ch2 = CreateMockChannel("ch-2");

        _manager.RegisterChannel(ch1);
        _manager.RegisterChannel(ch2);

        await _manager.DisposeAsync();

        await ch1.Received(1).DisconnectAsync();
        await ch2.Received(1).DisconnectAsync();
        await ch1.Received(1).DisposeAsync();
        await ch2.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_ClearsChannels()
    {
        var ch1 = CreateMockChannel("ch-1");
        _manager.RegisterChannel(ch1);

        await _manager.DisposeAsync();

        Assert.False(_manager.TryGetChannel("ch-1", out _));
        Assert.Empty(_manager.GetAllChannels());
    }

    [Fact]
    public async Task MessageReceived_Event_PropagatedFromChannel()
    {
        var channel = CreateMockChannel("ch-1");
        _manager.RegisterChannel(channel);

        var testMessage = CreateTestInboundMessage("ch-1");

        IChannel? receivedChannel = null;
        InboundMessage? receivedMessage = null;
        _manager.MessageReceived += (ch, msg) =>
        {
            receivedChannel = ch;
            receivedMessage = msg;
            return Task.CompletedTask;
        };

        // Raise the event on the mock channel — this invokes all subscribers
        channel.MessageReceived += Raise.Event<Func<InboundMessage, Task>>(testMessage);

        Assert.NotNull(receivedChannel);
        Assert.NotNull(receivedMessage);
        Assert.Same(channel, receivedChannel);
        Assert.Equal("msg-1", receivedMessage.MessageId);
    }

    [Fact]
    public async Task ChannelStatusChanged_Event_PropagatedFromChannel()
    {
        var channel = CreateMockChannel("ch-1");
        _manager.RegisterChannel(channel);

        var statusChange = new ChannelStatusChange(
            ChannelStatus.Disconnected,
            ChannelStatus.Connected,
            Reason: "Initial connection"
        );

        IChannel? receivedChannel = null;
        ChannelStatusChange? receivedChange = null;
        _manager.ChannelStatusChanged += (ch, change) =>
        {
            receivedChannel = ch;
            receivedChange = change;
            return Task.CompletedTask;
        };

        // Raise the event on the mock channel
        channel.StatusChanged += Raise.Event<Func<ChannelStatusChange, Task>>(statusChange);

        Assert.NotNull(receivedChannel);
        Assert.NotNull(receivedChange);
        Assert.Same(channel, receivedChannel);
        Assert.Equal(ChannelStatus.Disconnected, receivedChange.PreviousStatus);
        Assert.Equal(ChannelStatus.Connected, receivedChange.CurrentStatus);
        Assert.Equal("Initial connection", receivedChange.Reason);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IChannel CreateMockChannel(
        string channelId,
        ChannelType type = ChannelType.WebChat)
    {
        var channel = Substitute.For<IChannel>();
        channel.ChannelId.Returns(channelId);
        channel.Type.Returns(type);
        channel.Status.Returns(ChannelStatus.Disconnected);
        channel.Capabilities.Returns(new ChannelCapabilities());
        return channel;
    }

    private static InboundMessage CreateTestInboundMessage(string channelId)
    {
        return new InboundMessage
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            ChannelId = channelId,
            ChannelType = ChannelType.WebChat,
            Sender = new SenderInfo { Id = "user-1", DisplayName = "Test User" },
            Content = new MessageContent { Text = "Hello" },
            Timestamp = DateTimeOffset.UtcNow,
        };
    }
}
