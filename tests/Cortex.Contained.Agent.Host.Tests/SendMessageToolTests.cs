using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Hubs;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Tools;
using Cortex.Contained.Agent.Host.Tools.BuiltIn;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public class SendMessageToolTests
{
    private static readonly ToolExecutionContext _context = new()
    {
        ConversationId = "conv-test",
        ChannelId = "webchat-default",
    };

    private readonly IHubClients<IAgentHubClient> _hubClients;
    private readonly BridgeClientAccessor _accessor;
    private readonly ActiveChannelStore _activeChannelStore;
    private readonly AgentSessionStore _sessionStore;
    private readonly SendMessageTool _tool;

    public SendMessageToolTests()
    {
        var hubContext = Substitute.For<IHubContext<AgentHub, IAgentHubClient>>();
        _hubClients = Substitute.For<IHubClients<IAgentHubClient>>();
        hubContext.Clients.Returns(_hubClients);
        _accessor = new BridgeClientAccessor(hubContext);
        _activeChannelStore = new ActiveChannelStore();
        _sessionStore = new AgentSessionStore(
            new SessionConfig(), new MemorySettingsStore(), NullLogger<AgentSessionStore>.Instance);
        var messageStore = new Cortex.Contained.Agent.Host.Storage.MessageStore(
            ":memory:", NullLogger<Cortex.Contained.Agent.Host.Storage.MessageStore>.Instance);
        // Real dispatcher wrapping the test BridgeClientAccessor + MessageStore — exercises
        // the same code path production uses, just with the test fakes.
        var dispatcher = new Cortex.Contained.Agent.Host.Tools.ProactiveMessageDispatcher(
            _accessor,
            messageStore,
            NullLogger<Cortex.Contained.Agent.Host.Tools.ProactiveMessageDispatcher>.Instance);
        _tool = new SendMessageTool(_activeChannelStore, dispatcher);
    }

    private void SetClient(IAgentHubClient mockClient, string connectionId = "conn-1")
    {
        _hubClients.Client(connectionId).Returns(mockClient);
        _accessor.SetConnectionId(connectionId);
    }

    [Fact]
    public void Name_IsSendMessage()
    {
        Assert.Equal("send_message", _tool.Name);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(_tool.Description));
    }

    [Fact]
    public void ParametersSchema_IsValidJson()
    {
        var doc = System.Text.Json.JsonDocument.Parse(_tool.ParametersSchema);
        Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
        // Verify 'channel' property exists instead of old 'conversation_id'
        Assert.True(doc.RootElement.GetProperty("properties").TryGetProperty("channel", out _));
        Assert.False(doc.RootElement.GetProperty("properties").TryGetProperty("conversation_id", out _));
        // Verify 'channel' is required
        var required = doc.RootElement.GetProperty("required");
        var requiredValues = required.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("text", requiredValues);
        Assert.Contains("channel", requiredValues);
    }

    [Fact]
    public async Task ExecuteAsync_NoBridgeConnected_ReturnsError()
    {
        // No client set on accessor
        var args = """{"text":"Hello","channel":"webchat"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Bridge is not connected", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NoChannelSpecified_ReturnsErrorWithHint()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        SetClient(mockClient);

        var args = """{"text":"Hello from context"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: channel", result.Error);
        // Should hint the user's current channel (webchat) derived from context.ChannelId
        Assert.Contains("webchat", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_NoChannelSpecified_ScheduledContext_ReturnsErrorWithoutHint()
    {
        var contextWithScheduledChannel = new ToolExecutionContext
        {
            ConversationId = "scheduled-abc",
            ChannelId = "scheduled", // no explicit target channel
        };

        var mockClient = Substitute.For<IAgentHubClient>();
        SetClient(mockClient);

        var args = """{"text":"Hello scheduled"}""";

        var result = await _tool.ExecuteAsync(args, contextWithScheduledChannel, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Missing required parameter: channel", result.Error);
        // "scheduled" is not a real channel, so no friendly-name hint should appear
        Assert.DoesNotContain("currently on", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WithChannel_ResolvesAndSetsChannelId()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"discord"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m =>
                m.Text == "Hello" && m.ChannelId == "discord-dm"));
    }

    [Fact]
    public async Task ExecuteAsync_WithFullChannelName_ResolvesCorrectly()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"webchat-default"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m => m.ChannelId == "webchat-default"));
    }

    [Fact]
    public async Task ExecuteAsync_WithInvalidChannel_ReturnsError()
    {
        var args = """{"text":"Hello","channel":"invalid-channel"}""";

        var mockClient = Substitute.For<IAgentHubClient>();
        SetClient(mockClient);

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Unknown channel", result.Error);
        Assert.Contains("invalid-channel", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WithText_SendsMessage()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Override test","channel":"webchat"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m =>
                m.Text == "Override test"));
    }

    [Fact]
    public async Task ExecuteAsync_MissingText_ReturnsError()
    {
        var args = """{}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("text", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_WithTextAndChannel_SendsSuccessfully()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"webchat"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m => m.Text == "Hello"));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyText_ReturnsError()
    {
        var args = """{"text":"   "}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("text cannot be empty", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsError()
    {
        var result = await _tool.ExecuteAsync("not json", _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Invalid JSON", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulSend_ReturnsSuccess()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult
            {
                Success = true,
                ConversationId = "conv-42",
            });
        SetClient(mockClient);

        var args = """{"text":"Hello from agent!","channel":"webchat"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("Message sent successfully", result.Content);
        Assert.Contains("conv-42", result.Content);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m =>
                m.Text == "Hello from agent!"));
    }

    [Fact]
    public async Task ExecuteAsync_BridgeReturnsError_ReturnsError()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult
            {
                Success = false,
                Error = "Channel not found",
            });
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"webchat"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Channel not found", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_SetsCorrelationId()
    {
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Test","channel":"webchat"}""";
        await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        await mockClient.Received(1).OnProactiveMessage(
            Arg.Is<ProactiveMessage>(m => m.CorrelationId != null && m.CorrelationId.Length > 0));
    }

    // ── Active Channel Validation ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ChannelNotActive_ReturnsError()
    {
        _activeChannelStore.Set(["webchat-default"]);

        var mockClient = Substitute.For<IAgentHubClient>();
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"discord"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("not currently active", result.Error);
        Assert.Contains("discord", result.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ChannelIsActive_Succeeds()
    {
        _activeChannelStore.Set(["webchat-default", "discord-dm"]);

        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"discord"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_NoActiveChannelsSet_AllowsAnyChannel()
    {
        // ActiveChannelStore default is empty — should allow all channels (graceful degradation)
        var mockClient = Substitute.For<IAgentHubClient>();
        mockClient.OnProactiveMessage(Arg.Any<ProactiveMessage>())
            .Returns(new ProactiveMessageResult { Success = true });
        SetClient(mockClient);

        var args = """{"text":"Hello","channel":"voice"}""";

        var result = await _tool.ExecuteAsync(args, _context, CancellationToken.None);

        Assert.True(result.Success);
    }

    // ── Dynamic Description / ParametersSchema ──────────────────────────

    [Fact]
    public void Description_NoActiveChannels_ShowsAllChannels()
    {
        var desc = _tool.Description;

        Assert.Contains("webchat", desc);
        Assert.Contains("discord", desc);
        Assert.Contains("voice", desc);
    }

    [Fact]
    public void Description_WithActiveChannels_ShowsOnlyActiveChannels()
    {
        _activeChannelStore.Set(["webchat-default"]);

        var desc = _tool.Description;

        Assert.Contains("Available channels:", desc);
        Assert.Contains("webchat", desc);
        Assert.DoesNotContain("discord", desc);
        Assert.DoesNotContain("voice", desc);
    }

    [Fact]
    public void ParametersSchema_WithActiveChannels_IncludesActiveChannelNames()
    {
        _activeChannelStore.Set(["discord-dm", "discord-guild"]);

        var schema = _tool.ParametersSchema;

        Assert.Contains("discord", schema);
        Assert.Contains("discord-dm", schema);
        Assert.Contains("discord-guild", schema);
        Assert.DoesNotContain("webchat", schema);
        Assert.DoesNotContain("voice", schema);
    }

    [Fact]
    public void ParametersSchema_DynamicallyUpdatesWhenActiveChannelsChange()
    {
        _activeChannelStore.Set(["webchat-default"]);
        var schema1 = _tool.ParametersSchema;
        Assert.Contains("webchat", schema1);
        Assert.DoesNotContain("discord", schema1);

        _activeChannelStore.Set(["discord-dm"]);
        var schema2 = _tool.ParametersSchema;
        Assert.Contains("discord", schema2);
        Assert.DoesNotContain("webchat", schema2);
    }
}
