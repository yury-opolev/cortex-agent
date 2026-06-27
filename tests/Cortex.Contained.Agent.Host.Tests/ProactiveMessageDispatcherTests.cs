using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;

namespace Cortex.Contained.Agent.Host.Tests;

public class ProactiveMessageDispatcherTests : IAsyncDisposable
{
    private readonly IHubClients<IAgentHubClient> hubClients;
    private readonly BridgeClientAccessor accessor;
    private readonly MessageStore messageStore;
    private readonly ProactiveMessageDispatcher dispatcher;

    public ProactiveMessageDispatcherTests()
    {
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        this.hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(this.hubClients);
        this.accessor = new BridgeClientAccessor(hubContext);
        this.messageStore = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);
        this.dispatcher = new ProactiveMessageDispatcher(
            this.accessor,
            this.messageStore,
            NullLogger<ProactiveMessageDispatcher>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.messageStore.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private IAgentHubClient SetClient(string connectionId = "conn-1")
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        this.hubClients.Client(connectionId).Returns(mockClient);
        this.accessor.SetConnectionId(connectionId);
        return mockClient;
    }

    [Fact]
    public async Task DispatchAsync_EmptyChannelId_ReturnsError()
    {
        var result = await this.dispatcher.DispatchAsync("", "hello", context: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("channelId is required", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_EmptyText_ReturnsError()
    {
        var result = await this.dispatcher.DispatchAsync("voice-default", "  ", context: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("text is required", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_NoBridgeConnected_ReturnsError()
    {
        var result = await this.dispatcher.DispatchAsync("voice-default", "hello", context: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Bridge is not connected", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_BridgeReturnsFailure_ReturnsErrorWithBridgeReason()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = false, Error = "channel not found" });

        var result = await this.dispatcher.DispatchAsync("voice-default", "hello", context: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("channel not found", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_BridgeThrows_ReturnsError()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Throws(new InvalidOperationException("SignalR pipe broken"));

        var result = await this.dispatcher.DispatchAsync("voice-default", "hello", context: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("SignalR pipe broken", result.Error);
    }

    [Fact]
    public async Task DispatchAsync_Success_CallsBridgeWithCorrectPayload()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true, ConversationId = "conv-9" });

        var result = await this.dispatcher.DispatchAsync("voice-default", "ring ring", context: null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("conv-9", result.ConversationId);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m =>
                m.ChannelId == "voice-default"
                && m.Text == "ring ring"
                && !string.IsNullOrEmpty(m.CorrelationId)));
    }

    [Fact]
    public async Task DispatchAsync_Success_PersistsToMessageStore()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });

        await this.dispatcher.DispatchAsync("voice-default", "hello", context: null, CancellationToken.None);

        var stored = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        var msg = Assert.Single(stored, m => m.Category == MessageCategory.Proactive);
        Assert.Equal("hello", msg.Content);
        Assert.Equal("assistant", msg.Role);
    }

    [Fact]
    public async Task DispatchAsync_Success_UsesContextCorrelationId()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });

        var context = new ToolExecutionContext
        {
            ConversationId = "webchat-default",
            ChannelId = "webchat-default",
            CorrelationId = "corr-X",
        };

        await this.dispatcher.DispatchAsync("voice-default", "hello", context, CancellationToken.None);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m => m.CorrelationId == "corr-X"));
    }

    [Fact]
    public async Task DispatchAsync_Success_NoContextCorrelationId_MintsFreshId()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });

        var context = new ToolExecutionContext
        {
            ConversationId = "webchat-default",
            ChannelId = "webchat-default",
            // CorrelationId left null.
        };

        await this.dispatcher.DispatchAsync("voice-default", "hello", context, CancellationToken.None);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m => !string.IsNullOrEmpty(m.CorrelationId)));
    }

    [Fact]
    public async Task DispatchAsync_Success_RecordsToContextWhenProvided()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });

        var context = new ToolExecutionContext
        {
            ConversationId = "webchat-default",
            ChannelId = "webchat-default",
        };

        await this.dispatcher.DispatchAsync("voice-default", "hello", context, CancellationToken.None);

        var record = Assert.Single(context.ProactiveMessages.Collected);
        Assert.Equal("voice-default", record.ChannelId);
        Assert.Equal("hello", record.Text);
    }

    [Fact]
    public async Task DispatchAsync_Success_WithNullContext_DoesNotThrow()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });

        var result = await this.dispatcher.DispatchAsync("voice-default", "hello", context: null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        // Verify the MessageStore write still happened even though context was null.
        var stored = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        Assert.Single(stored, m => m.Category == MessageCategory.Proactive);
    }

    [Fact]
    public async Task DispatchAsync_BridgeFailure_DoesNotPersistOrRecord()
    {
        var mockClient = SetClient();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = false, Error = "nope" });

        var context = new ToolExecutionContext
        {
            ConversationId = "webchat-default",
            ChannelId = "webchat-default",
        };

        await this.dispatcher.DispatchAsync("voice-default", "hello", context, CancellationToken.None);

        var stored = await this.messageStore.GetMessagesAsync("voice-default", limit: 10);
        Assert.DoesNotContain(stored, m => m.Category == MessageCategory.Proactive);
        Assert.Empty(context.ProactiveMessages.Collected);
    }
}
