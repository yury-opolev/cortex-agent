using System.Reflection;
using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests;

public class HubMessageDispatcherTests : IAsyncDisposable
{
    private readonly ChannelManager _channelManager;
    private readonly HubClient _hubClient;
    private readonly HubMessageDispatcher _dispatcher;

    public HubMessageDispatcherTests()
    {
        _channelManager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        _hubClient = new HubClient(NullLogger<HubClient>.Instance);

        _dispatcher = new HubMessageDispatcher(
            _channelManager,
            CreateEmptyTenantRouter(),
            NullLogger<HubMessageDispatcher>.Instance);
        _dispatcher.Initialize();
        _dispatcher.WireHubClient(_hubClient, "default");
    }

    public async ValueTask DisposeAsync()
    {
        await _channelManager.DisposeAsync();
        await _hubClient.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // ── Proactive Messaging Tests ────────────────────────────────────────

    [Fact]
    public async Task OnProactiveMessage_WithExplicitChannelId_RoutesToSpecifiedChannel()
    {
        var discordChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        discordChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });

        var webChatChannel = CreateMockChannel("webchat-default");
        webChatChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });

        _channelManager.RegisterChannel(discordChannel);
        _channelManager.RegisterChannel(webChatChannel);

        var message = new ProactiveMessage
        {
            Text = "Hello from agent!",
            ChannelId = "discord-dm",
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.True(result.Success);
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.Content.Text == "Hello from agent!" && m.ChannelId == "discord-dm"),
            Arg.Any<CancellationToken>());
        await webChatChannel.DidNotReceive().SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnProactiveMessage_WithAttachments_ForwardsToOutboundMessage()
    {
        var discordChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        discordChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        _channelManager.RegisterChannel(discordChannel);

        var message = new ProactiveMessage
        {
            Text = "see chart",
            ChannelId = "discord-dm",
            Attachments = new List<MediaAttachment>
            {
                new() { MimeType = "image/png", FileName = "chart.png", Data = [1, 2, 3] },
            },
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.True(result.Success);
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m =>
                m.Content.Attachments != null
                && m.Content.Attachments.Count == 1
                && m.Content.Attachments[0].FileName == "chart.png"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnProactiveMessage_WithoutChannel_ReturnsError()
    {
        var channel = CreateMockChannel("discord-dm", ChannelType.Discord);
        _channelManager.RegisterChannel(channel);

        var message = new ProactiveMessage
        {
            Text = "No channel specified",
            ChannelId = null,
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.False(result.Success);
        Assert.Contains("No target channel specified", result.Error);
    }

    [Fact]
    public async Task OnProactiveMessage_UnknownChannel_ReturnsError()
    {
        var channel = CreateMockChannel("discord-dm", ChannelType.Discord);
        _channelManager.RegisterChannel(channel);

        var message = new ProactiveMessage
        {
            Text = "Wrong channel",
            ChannelId = "nonexistent-channel",
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task OnProactiveMessage_ChannelSendFailure_ReturnsError()
    {
        var channel = CreateMockChannel("discord-dm", ChannelType.Discord);
        channel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = false, ErrorMessage = "Discord API error" });
        _channelManager.RegisterChannel(channel);

        var message = new ProactiveMessage
        {
            Text = "Will fail",
            ChannelId = "discord-dm",
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.False(result.Success);
        Assert.Contains("delivery failed", result.Error);
    }

    [Fact]
    public async Task OnProactiveMessage_DiscordVoice_SendsWithTenantPrefixedConversationId()
    {
        // Regression: DiscordChannel.SendMessageAsync only routes to the voice
        // handler (→ TTS) when ConversationId matches "discord-voice-{tenantId}".
        // The proactive flow previously used a random GUID, so audio never played.
        //
        // The setup below mirrors production: one DiscordChannel instance
        // registered under the primary ID "discord-dm" with "discord-voice" as
        // an alias. A previous version of the fix checked `channel.ChannelId`
        // (always "discord-dm") and silently fell through to text delivery;
        // the correct check is the requested `message.ChannelId`.
        var discordChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        discordChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });

        _channelManager.RegisterChannel(discordChannel);
        _channelManager.RegisterChannelAlias("discord-voice", discordChannel);

        var message = new ProactiveMessage
        {
            Text = "Eggs are cheap at Lidl this week.",
            ChannelId = "discord-voice",
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.True(result.Success);
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == "discord-voice-default"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnProactiveMessage_SetsConversationChannelMap()
    {
        var channel = CreateMockChannel("discord-dm", ChannelType.Discord);
        channel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        _channelManager.RegisterChannel(channel);

        var message = new ProactiveMessage
        {
            Text = "Setup mapping",
            ChannelId = "discord-dm",
            ConversationId = "proactive-conv-1",
        };

        var result = await RaiseProactiveMessageEvent(message);

        Assert.True(result.Success);
        Assert.NotNull(result.ConversationId);
    }

    // ── Inbound Message Tests ────────────────────────────────────────────

    [Fact]
    public async Task InboundMessage_MapsConversationToChannel()
    {
        var channel = CreateMockChannel("discord-main", ChannelType.Discord);
        channel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        _channelManager.RegisterChannel(channel);

        await SimulateInboundMessage(channel, "discord-main", "conv-1");

        var message = new ProactiveMessage
        {
            Text = "After inbound",
            ChannelId = "discord-main",
        };

        var result = await RaiseProactiveMessageEvent(message);
        Assert.True(result.Success);
    }

    // ── Multi-Tenant Outbound Routing Tests ──────────────────────────────

    [Fact]
    public void WireHubClient_SameClientTwice_NoDuplicateHandlers()
    {
        var client = new HubClient(NullLogger<HubClient>.Instance);
        var channelManager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        var dispatcher = new HubMessageDispatcher(
            channelManager,
            CreateEmptyTenantRouter(),
            NullLogger<HubMessageDispatcher>.Instance);

        // Should not throw or cause duplicate events
        dispatcher.WireHubClient(client, "tenant-a");
        dispatcher.WireHubClient(client, "tenant-a");
    }

    [Fact]
    public async Task OutboundResponse_RewritesDmConversationIdToSnowflake()
    {
        var (dispatcher, channelManager, tenantRouter) = CreateTenantAwareDispatcher(
            ("tenant-a", "user-123"));

        var discordChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        discordChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        channelManager.RegisterChannel(discordChannel);

        var hubClient = new HubClient(NullLogger<HubClient>.Instance);
        dispatcher.WireHubClient(hubClient, "tenant-a");

        // Simulate inbound DM to cache the snowflake
        await SimulateInboundDmWithSnowflake(discordChannel, "user-123", 98765);

        // Fire proactive from tenant-a targeting discord-dm
        var proactive = new ProactiveMessage
        {
            Text = "Hello from tenant!",
            ChannelId = "discord-dm",
            ConversationId = "discord-dm",
        };

        var result = await RaiseProactiveMessageEventOn(hubClient, proactive);

        Assert.True(result.Success);
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == "98765"),
            Arg.Any<CancellationToken>());

        await channelManager.DisposeAsync();
        await hubClient.DisposeAsync();
    }

    [Fact]
    public async Task OutboundResponse_NoCachedSnowflake_KeepsOriginalConversationId()
    {
        var channelManager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        var dispatcher = new HubMessageDispatcher(
            channelManager,
            CreateEmptyTenantRouter(),
            NullLogger<HubMessageDispatcher>.Instance);
        dispatcher.Initialize();

        var discordChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        discordChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        channelManager.RegisterChannel(discordChannel);

        var hubClient = new HubClient(NullLogger<HubClient>.Instance);
        dispatcher.WireHubClient(hubClient, "tenant-b");

        // No inbound DM → no cached snowflake
        var proactive = new ProactiveMessage
        {
            Text = "No snowflake cached",
            ChannelId = "discord-dm",
            ConversationId = "discord-dm",
        };

        var result = await RaiseProactiveMessageEventOn(hubClient, proactive);

        Assert.True(result.Success);
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == "discord-dm"),
            Arg.Any<CancellationToken>());

        await channelManager.DisposeAsync();
        await hubClient.DisposeAsync();
    }

    [Fact]
    public async Task OutboundResponse_NonDmConversationId_NoRewrite()
    {
        var (dispatcher, channelManager, _) = CreateTenantAwareDispatcher(
            ("tenant-a", "user-123"));

        var guildChannel = CreateMockChannel("discord-guild", ChannelType.Discord);
        guildChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        channelManager.RegisterChannel(guildChannel);

        // Also register discord-dm and cache a snowflake
        var dmChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        channelManager.RegisterChannel(dmChannel);

        var hubClient = new HubClient(NullLogger<HubClient>.Instance);
        dispatcher.WireHubClient(hubClient, "tenant-a");
        await SimulateInboundDmWithSnowflake(dmChannel, "user-123", 98765);

        // Send to guild — should NOT rewrite
        var proactive = new ProactiveMessage
        {
            Text = "Guild message",
            ChannelId = "discord-guild",
            ConversationId = "discord-guild",
        };

        var result = await RaiseProactiveMessageEventOn(hubClient, proactive);

        Assert.True(result.Success);
        await guildChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == "discord-guild"),
            Arg.Any<CancellationToken>());

        await channelManager.DisposeAsync();
        await hubClient.DisposeAsync();
    }

    [Fact]
    public async Task WireHubClient_MultipleTenants_IndependentRouting()
    {
        var (dispatcher, channelManager, _) = CreateTenantAwareDispatcher(
            ("tenant-alice", "alice-id"),
            ("tenant-bob", "bob-id"));

        var discordChannel = CreateMockChannel("discord-dm", ChannelType.Discord);
        discordChannel.SendMessageAsync(Arg.Any<OutboundMessage>(), Arg.Any<CancellationToken>())
            .Returns(new SendResult { Success = true });
        channelManager.RegisterChannel(discordChannel);

        var clientAlice = new HubClient(NullLogger<HubClient>.Instance);
        var clientBob = new HubClient(NullLogger<HubClient>.Instance);
        dispatcher.WireHubClient(clientAlice, "tenant-alice");
        dispatcher.WireHubClient(clientBob, "tenant-bob");

        // Simulate inbound DMs to cache snowflakes
        await SimulateInboundDmWithSnowflake(discordChannel, "alice-id", 11111);
        await SimulateInboundDmWithSnowflake(discordChannel, "bob-id", 22222);

        // Alice's agent sends message
        var aliceResult = await RaiseProactiveMessageEventOn(clientAlice, new ProactiveMessage
        {
            Text = "Hello Alice!",
            ChannelId = "discord-dm",
            ConversationId = "discord-dm",
        });

        // Bob's agent sends message
        var bobResult = await RaiseProactiveMessageEventOn(clientBob, new ProactiveMessage
        {
            Text = "Hello Bob!",
            ChannelId = "discord-dm",
            ConversationId = "discord-dm",
        });

        Assert.True(aliceResult.Success);
        Assert.True(bobResult.Success);

        // Alice → snowflake 11111
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == "11111" && m.Content.Text == "Hello Alice!"),
            Arg.Any<CancellationToken>());

        // Bob → snowflake 22222
        await discordChannel.Received(1).SendMessageAsync(
            Arg.Is<OutboundMessage>(m => m.ConversationId == "22222" && m.Content.Text == "Hello Bob!"),
            Arg.Any<CancellationToken>());

        await channelManager.DisposeAsync();
        await clientAlice.DisposeAsync();
        await clientBob.DisposeAsync();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IChannel CreateMockChannel(
        string channelId,
        ChannelType type = ChannelType.WebChat)
    {
        var channel = Substitute.For<IChannel>();
        channel.ChannelId.Returns(channelId);
        channel.Type.Returns(type);
        channel.Status.Returns(ChannelStatus.Connected);
        channel.Capabilities.Returns(new ChannelCapabilities());
        return channel;
    }

    private static async Task SimulateInboundMessage(IChannel channel, string channelId, string conversationId)
    {
        var inbound = new InboundMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            ChannelId = channelId,
            ChannelType = channel.Type,
            Sender = new SenderInfo { Id = "test-user" },
            Content = new MessageContent { Text = "test" },
            Timestamp = DateTimeOffset.UtcNow,
        };

        channel.MessageReceived += Raise.Event<Func<InboundMessage, Task>>(inbound);
        await Task.Delay(50);
    }

    private static async Task SimulateInboundDmWithSnowflake(IChannel channel, string senderId, ulong snowflake)
    {
        var inbound = new InboundMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ConversationId = "discord-dm",
            ChannelId = "discord-dm",
            ChannelType = ChannelType.Discord,
            Sender = new SenderInfo { Id = senderId },
            Content = new MessageContent { Text = "hello" },
            Timestamp = DateTimeOffset.UtcNow,
            Properties = new Dictionary<string, string> { ["dm_snowflake"] = snowflake.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

        channel.MessageReceived += Raise.Event<Func<InboundMessage, Task>>(inbound);
        await Task.Delay(50);
    }

    /// <summary>
    /// Creates a TenantRouter with no tenants (for tests that don't need tenant resolution).
    /// </summary>
    private static TenantRouter CreateEmptyTenantRouter()
    {
        var config = new BridgeConfig();
        var registry = new TenantRegistry(config, () => { }, NullLogger<TenantRegistry>.Instance);
        return new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);
    }

    /// <summary>
    /// Creates a full dispatcher + channel manager + tenant router with configured tenants.
    /// Each tuple is (tenantId, discordUserId).
    /// </summary>
    private static (HubMessageDispatcher Dispatcher, ChannelManager ChannelManager, TenantRouter Router) CreateTenantAwareDispatcher(
        params (string TenantId, string DiscordUserId)[] tenants)
    {
        var config = new BridgeConfig();
        foreach (var (tenantId, discordUserId) in tenants)
        {
            config.Tenants[tenantId] = new TenantConfig
            {
                Endpoint = $"http://localhost:5100/{tenantId}",
                Enabled = true,
                DiscordUserId = discordUserId,
            };
        }

        var registry = new TenantRegistry(config, () => { }, NullLogger<TenantRegistry>.Instance);
        var router = new TenantRouter(registry, NullLoggerFactory.Instance, NullLogger<TenantRouter>.Instance);
        var channelManager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        var dispatcher = new HubMessageDispatcher(
            channelManager,
            router,
            NullLogger<HubMessageDispatcher>.Instance);
        dispatcher.Initialize();

        return (dispatcher, channelManager, router);
    }

    private async Task<ProactiveMessageResult> RaiseProactiveMessageEvent(ProactiveMessage message)
    {
        return await RaiseProactiveMessageEventOn(_hubClient, message);
    }

    private static async Task<ProactiveMessageResult> RaiseProactiveMessageEventOn(HubClient client, ProactiveMessage message)
    {
        // The OnProactiveMessage event's backing field is private.
        // Search with both NonPublic and Public flags to cover field-like events.
        var eventField = typeof(HubClient).GetField(
            nameof(HubClient.OnProactiveMessage),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(eventField);

        var fieldValue = eventField!.GetValue(client);
        Assert.NotNull(fieldValue);

        // The event may be a multicast delegate — invoke it directly.
        var handler = (Delegate)fieldValue!;
        var result = handler.DynamicInvoke(message);
        Assert.NotNull(result);

        return await (Task<ProactiveMessageResult>)result!;
    }
}
