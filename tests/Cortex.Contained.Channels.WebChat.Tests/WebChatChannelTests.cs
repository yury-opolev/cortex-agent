using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Channels.WebChat.Tests;

public class WebChatChannelTests : IAsyncDisposable
{
    private readonly WebChatChannel _sut;

    public WebChatChannelTests()
    {
        _sut = new WebChatChannel(NullLogger<WebChatChannel>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    #region Constructor and Properties

    [Fact]
    public void Constructor_DefaultChannelId_SetsWebChatDefault()
    {
        Assert.Equal("webchat-default", _sut.ChannelId);
    }

    [Fact]
    public void Constructor_CustomChannelId_SetsProvidedValue()
    {
        var channel = new WebChatChannel(NullLogger<WebChatChannel>.Instance, "my-channel");

        Assert.Equal("my-channel", channel.ChannelId);
    }

    [Fact]
    public void Type_ReturnsWebChat()
    {
        Assert.Equal(ChannelType.WebChat, _sut.Type);
    }

    [Fact]
    public void Status_InitiallyDisconnected()
    {
        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    #endregion

    #region Capabilities

    [Fact]
    public void Capabilities_SupportsStreaming_IsTrue()
    {
        Assert.True(_sut.Capabilities.SupportsStreaming);
    }

    [Fact]
    public void Capabilities_SupportsRichText_IsTrue()
    {
        Assert.True(_sut.Capabilities.SupportsRichText);
    }

    [Fact]
    public void Capabilities_SupportsEditing_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsEditing);
    }

    [Fact]
    public void Capabilities_SupportsDeletion_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsDeletion);
    }

    [Fact]
    public void Capabilities_SupportsMedia_IsFalse()
    {
        Assert.False(_sut.Capabilities.SupportsMedia);
    }

    [Fact]
    public void Capabilities_MaxMessageLength_Is100000()
    {
        Assert.Equal(100_000, _sut.Capabilities.MaxMessageLength);
    }

    #endregion

    #region ConnectAsync

    [Fact]
    public async Task ConnectAsync_SetsStatusToConnected()
    {
        await _sut.ConnectAsync();

        Assert.Equal(ChannelStatus.Connected, _sut.Status);
    }

    [Fact]
    public async Task ConnectAsync_FiresStatusChangedEvent_WithCorrectTransition()
    {
        ChannelStatusChange? captured = null;
        _sut.StatusChanged += change =>
        {
            captured = change;
            return Task.CompletedTask;
        };

        await _sut.ConnectAsync();

        Assert.NotNull(captured);
        Assert.Equal(ChannelStatus.Disconnected, captured.PreviousStatus);
        Assert.Equal(ChannelStatus.Connected, captured.CurrentStatus);
        Assert.Equal("Channel connected", captured.Reason);
    }

    #endregion

    #region DisconnectAsync

    [Fact]
    public async Task DisconnectAsync_SetsStatusToDisconnected()
    {
        await _sut.ConnectAsync();

        await _sut.DisconnectAsync();

        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    [Fact]
    public async Task DisconnectAsync_FiresStatusChangedEvent_WithCorrectTransition()
    {
        await _sut.ConnectAsync();

        ChannelStatusChange? captured = null;
        _sut.StatusChanged += change =>
        {
            captured = change;
            return Task.CompletedTask;
        };

        await _sut.DisconnectAsync();

        Assert.NotNull(captured);
        Assert.Equal(ChannelStatus.Connected, captured.PreviousStatus);
        Assert.Equal(ChannelStatus.Disconnected, captured.CurrentStatus);
        Assert.Equal("Channel disconnected", captured.Reason);
    }

    #endregion

    #region SendMessageAsync

    [Fact]
    public async Task SendMessageAsync_RaisesOutboundMessageReadyEvent()
    {
        OutboundMessage? captured = null;
        _sut.OutboundMessageReady += msg =>
        {
            captured = msg;
            return Task.CompletedTask;
        };

        var message = CreateOutboundMessage("msg-1", "conv-1");
        await _sut.SendMessageAsync(message);

        Assert.NotNull(captured);
        Assert.Equal("msg-1", captured.MessageId);
        Assert.Equal("conv-1", captured.ConversationId);
    }

    [Fact]
    public async Task SendMessageAsync_ReturnsSuccessWithMessageId()
    {
        var message = CreateOutboundMessage("msg-42", "conv-1");

        var result = await _sut.SendMessageAsync(message);

        Assert.True(result.Success);
        Assert.Equal("msg-42", result.ExternalMessageId);
    }

    [Fact]
    public async Task SendMessageAsync_WithoutSubscriber_StillReturnsSuccess()
    {
        var message = CreateOutboundMessage("msg-99", "conv-1");

        var result = await _sut.SendMessageAsync(message);

        Assert.True(result.Success);
        Assert.Equal("msg-99", result.ExternalMessageId);
    }

    #endregion

    #region SendTypingIndicatorAsync

    [Fact]
    public async Task SendTypingIndicatorAsync_RaisesTypingIndicatorReadyEvent()
    {
        string? capturedConversationId = null;
        _sut.TypingIndicatorReady += convId =>
        {
            capturedConversationId = convId;
            return Task.CompletedTask;
        };

        await _sut.SendTypingIndicatorAsync("conv-abc");

        Assert.Equal("conv-abc", capturedConversationId);
    }

    [Fact]
    public async Task SendTypingIndicatorAsync_WithoutSubscriber_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() => _sut.SendTypingIndicatorAsync("conv-1"));

        Assert.Null(exception);
    }

    #endregion

    #region SendStreamingUpdateAsync

    [Fact]
    public async Task SendStreamingUpdateAsync_RaisesStreamingUpdateReadyEvent_WithCorrectArgs()
    {
        string? capturedConversationId = null;
        string? capturedPartialText = null;
        _sut.StreamingUpdateReady += (convId, partial) =>
        {
            capturedConversationId = convId;
            capturedPartialText = partial;
            return Task.CompletedTask;
        };

        await _sut.SendStreamingUpdateAsync("conv-stream", "Hello, this is partial...");

        Assert.Equal("conv-stream", capturedConversationId);
        Assert.Equal("Hello, this is partial...", capturedPartialText);
    }

    [Fact]
    public async Task SendStreamingUpdateAsync_WithoutSubscriber_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(
            () => _sut.SendStreamingUpdateAsync("conv-1", "partial text"));

        Assert.Null(exception);
    }

    #endregion

    #region FinalizeStreamingAsync

    [Fact]
    public async Task FinalizeStreamingAsync_RaisesStreamingFinalizeReadyEvent()
    {
        string? capturedConversationId = null;
        OutboundMessage? capturedMessage = null;
        _sut.StreamingFinalizeReady += (convId, msg) =>
        {
            capturedConversationId = convId;
            capturedMessage = msg;
            return Task.CompletedTask;
        };

        var finalMessage = CreateOutboundMessage("final-msg", "conv-final");
        await _sut.FinalizeStreamingAsync("conv-final", finalMessage);

        Assert.Equal("conv-final", capturedConversationId);
        Assert.NotNull(capturedMessage);
        Assert.Equal("final-msg", capturedMessage.MessageId);
    }

    [Fact]
    public async Task FinalizeStreamingAsync_WithoutSubscriber_DoesNotThrow()
    {
        var finalMessage = CreateOutboundMessage("msg-1", "conv-1");

        var exception = await Record.ExceptionAsync(
            () => _sut.FinalizeStreamingAsync("conv-1", finalMessage));

        Assert.Null(exception);
    }

    #endregion

    #region ReceiveFromBrowserAsync

    [Fact]
    public async Task ReceiveFromBrowserAsync_RaisesMessageReceivedEvent()
    {
        InboundMessage? captured = null;
        _sut.MessageReceived += msg =>
        {
            captured = msg;
            return Task.CompletedTask;
        };

        var inbound = CreateInboundMessage("inbound-1", "conv-browser");
        await _sut.ReceiveFromBrowserAsync(inbound);

        Assert.NotNull(captured);
        Assert.Equal("inbound-1", captured.MessageId);
        Assert.Equal("conv-browser", captured.ConversationId);
    }

    [Fact]
    public async Task ReceiveFromBrowserAsync_WithoutSubscriber_DoesNotThrow()
    {
        var inbound = CreateInboundMessage("inbound-1", "conv-1");

        var exception = await Record.ExceptionAsync(() => _sut.ReceiveFromBrowserAsync(inbound));

        Assert.Null(exception);
    }

    #endregion

    #region DisposeAsync

    [Fact]
    public async Task DisposeAsync_WhenConnected_DisconnectsAndSetsStatusToDisconnected()
    {
        await _sut.ConnectAsync();
        Assert.Equal(ChannelStatus.Connected, _sut.Status);

        await _sut.DisposeAsync();

        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    [Fact]
    public async Task DisposeAsync_WhenConnected_FiresStatusChangedEvent()
    {
        await _sut.ConnectAsync();

        ChannelStatusChange? captured = null;
        _sut.StatusChanged += change =>
        {
            captured = change;
            return Task.CompletedTask;
        };

        await _sut.DisposeAsync();

        Assert.NotNull(captured);
        Assert.Equal(ChannelStatus.Connected, captured.PreviousStatus);
        Assert.Equal(ChannelStatus.Disconnected, captured.CurrentStatus);
    }

    [Fact]
    public async Task DisposeAsync_WhenAlreadyDisconnected_DoesNotFireStatusChanged()
    {
        bool eventFired = false;
        _sut.StatusChanged += _ =>
        {
            eventFired = true;
            return Task.CompletedTask;
        };

        await _sut.DisposeAsync();

        Assert.False(eventFired);
    }

    [Fact]
    public async Task DisposeAsync_WhenAlreadyDisconnected_StatusRemainsDisconnected()
    {
        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);

        await _sut.DisposeAsync();

        Assert.Equal(ChannelStatus.Disconnected, _sut.Status);
    }

    #endregion

    #region Helpers

    private static OutboundMessage CreateOutboundMessage(string messageId, string conversationId) =>
        new()
        {
            MessageId = messageId,
            ConversationId = conversationId,
            ChannelId = "webchat-default",
            Content = new MessageContent { Text = "Test message" },
        };

    private static InboundMessage CreateInboundMessage(string messageId, string conversationId) =>
        new()
        {
            MessageId = messageId,
            ConversationId = conversationId,
            ChannelId = "webchat-default",
            ChannelType = ChannelType.WebChat,
            Sender = new SenderInfo { Id = "user-1", DisplayName = "Test User" },
            Content = new MessageContent { Text = "Hello from browser" },
            Timestamp = DateTimeOffset.UtcNow,
        };

    #endregion
}
