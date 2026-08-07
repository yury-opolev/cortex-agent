using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Bridge.Connectors;
using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class ConnectorHostTests
{
    private static (ConnectorHost host, ChannelManager manager) Build(
        bool enabled = true, int maxConnectors = 16)
    {
        var manager = new ChannelManager(NullLogger<ChannelManager>.Instance);
        var config = new BridgeConfig
        {
            Connectors = new ConnectorSettingsConfig
            {
                Enabled = enabled,
                MaxConnectors = maxConnectors,
            },
        };
        var host = new ConnectorHost(manager, config, NullLogger<ConnectorHost>.Instance);
        return (host, manager);
    }

    private static PluginChannel MakeChannel(string key = "terminal", string instanceId = "default") =>
        new(key, instanceId, new ChannelCapabilities(), key, NullLogger<PluginChannel>.Instance);

    // ── Attach ───────────────────────────────────────────────────────

    [Fact]
    public async Task TryAttachAsync_ValidChannel_ReturnsOkAndRegisters()
    {
        var (host, manager) = Build();
        var channel = MakeChannel();

        var result = await host.TryAttachAsync(channel, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(manager.TryGetChannel("plugin:terminal:default", out _));
    }

    // ── Disabled ─────────────────────────────────────────────────────

    [Fact]
    public async Task TryAttachAsync_Disabled_ReturnsDisabledCode()
    {
        var (host, _) = Build(enabled: false);
        var result = await host.TryAttachAsync(MakeChannel(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.Disabled, result.ErrorCode);
    }

    // ── Duplicate ────────────────────────────────────────────────────

    [Fact]
    public async Task TryAttachAsync_Duplicate_ReturnsDuplicateCode()
    {
        var (host, _) = Build();
        await host.TryAttachAsync(MakeChannel(), CancellationToken.None);
        var result = await host.TryAttachAsync(MakeChannel(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.Duplicate, result.ErrorCode);
    }

    // ── Limit ────────────────────────────────────────────────────────

    [Fact]
    public async Task TryAttachAsync_LimitReached_ReturnsConnectorLimitReached()
    {
        var (host, _) = Build(maxConnectors: 1);
        await host.TryAttachAsync(MakeChannel("bot", "a"), CancellationToken.None);
        var result = await host.TryAttachAsync(MakeChannel("bot", "b"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ConnectorErrorCodes.ConnectorLimitReached, result.ErrorCode);
    }

    // ── Detach ───────────────────────────────────────────────────────

    [Fact]
    public async Task DetachAsync_AttachedChannel_RemovesFromManager()
    {
        var (host, manager) = Build();
        var channel = MakeChannel();
        await host.TryAttachAsync(channel, CancellationToken.None);

        await host.DetachAsync(channel);

        Assert.False(manager.TryGetChannel("plugin:terminal:default", out _));
    }

    // ── Stale detach does not evict newer session's channel ──────────

    [Fact]
    public async Task DetachAsync_StaleReference_DoesNotEvictNewChannel()
    {
        var (host, manager) = Build(maxConnectors: 2);
        var channel1 = MakeChannel();
        await host.TryAttachAsync(channel1, CancellationToken.None);

        // Detach the first channel so a second can reuse the same id.
        await host.DetachAsync(channel1);

        var channel2 = MakeChannel();
        await host.TryAttachAsync(channel2, CancellationToken.None);

        // Stale teardown of channel1 must not evict channel2.
        await host.DetachAsync(channel1);

        Assert.True(manager.TryGetChannel("plugin:terminal:default", out _));
        Assert.Single(host.GetAttachedChannels());
    }

    // ── DetachAll ────────────────────────────────────────────────────

    [Fact]
    public async Task DetachAllAsync_DetachesEveryChannel()
    {
        var (host, manager) = Build();
        await host.TryAttachAsync(MakeChannel("a", "1"), CancellationToken.None);
        await host.TryAttachAsync(MakeChannel("b", "2"), CancellationToken.None);

        await host.DetachAllAsync("kill-switch");

        Assert.Empty(host.GetAttachedChannels());
    }

    // ── GetAttachedChannels ──────────────────────────────────────────

    [Fact]
    public async Task GetAttachedChannels_ReturnsSnapshot()
    {
        var (host, _) = Build();
        await host.TryAttachAsync(MakeChannel("c", "1"), CancellationToken.None);
        await host.TryAttachAsync(MakeChannel("d", "2"), CancellationToken.None);

        var snapshot = host.GetAttachedChannels();

        Assert.Equal(2, snapshot.Count);
    }
}
