using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Hosting;

public sealed class ChannelLifecycleManagerTests
{
    private static (ChannelLifecycleManager manager, ConnectorHost host) Build(BridgeConfig? config = null)
    {
        var channelManager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        var bridgeConfig = config ?? new BridgeConfig();
        var connectorHost = new ConnectorHost(channelManager, bridgeConfig, NullLogger<ConnectorHost>.Instance);

        var lcm = new ChannelLifecycleManager(
            tenantRouter: null!,
            tenantRegistry: null!,
            channelManager: channelManager,
            webChatChannel: null!,
            config: bridgeConfig,
            connectorHost: connectorHost,
            logger: NullLogger<ChannelLifecycleManager>.Instance);

        return (lcm, connectorHost);
    }

    private static PluginChannel MakeChannel(string key = "terminal", string instanceId = "default") =>
        new(key, instanceId, new ChannelCapabilities(), key, NullLogger<PluginChannel>.Instance);

    [Fact]
    public void BuildActiveChannelIds_NoChannelsConfigured_ReturnsEmpty()
    {
        var config = new BridgeConfig { WebUi = new() { Enabled = false } };
        var (lcm, _) = Build(config);

        var ids = lcm.BuildActiveChannelIds();

        Assert.Empty(ids);
    }

    [Fact]
    public async Task BuildActiveChannelIds_WithAttachedConnector_IncludesPluginChannelId()
    {
        var (lcm, host) = Build();
        await host.TryAttachAsync(MakeChannel("terminal", "default"), CancellationToken.None);

        var ids = lcm.BuildActiveChannelIds();

        Assert.Contains("plugin:terminal:default", ids);
    }

    [Fact]
    public async Task BuildActiveChannelIds_AfterDetach_ExcludesPluginChannelId()
    {
        var (lcm, host) = Build();
        var channel = MakeChannel("terminal", "default");
        await host.TryAttachAsync(channel, CancellationToken.None);
        await host.DetachAsync(channel);

        var ids = lcm.BuildActiveChannelIds();

        Assert.DoesNotContain("plugin:terminal:default", ids);
    }
}
