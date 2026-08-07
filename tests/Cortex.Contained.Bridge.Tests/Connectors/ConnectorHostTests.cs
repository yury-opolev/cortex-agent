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

    // ── AttachedCount ─────────────────────────────────────────────────

    [Fact]
    public async Task AttachedCount_ReflectsCurrentAttachedChannels()
    {
        var (host, _) = Build();
        Assert.Equal(0, host.AttachedCount);

        var ch1 = MakeChannel("e", "1");
        await host.TryAttachAsync(ch1, CancellationToken.None);
        Assert.Equal(1, host.AttachedCount);

        await host.DetachAsync(ch1);
        Assert.Equal(0, host.AttachedCount);
    }

    // ── Concurrent attach at limit — race safety ──────────────────────

    [Fact]
    public async Task TryAttachAsync_ConcurrentAtLimit_ExactlyLimitSucceed()
    {
        // Two sessions attaching simultaneously when one slot remains must result in
        // exactly one success. Verify the check+insert is atomic (the lock in ConnectorHost
        // covers both the count check and the insertion).
        const int limit = 10;
        const int concurrency = 50;
        var (host, _) = Build(maxConnectors: limit);

        var tasks = Enumerable.Range(0, concurrency).Select(i =>
            host.TryAttachAsync(MakeChannel("bot", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None).AsTask()).ToList();

        var results = await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.Success);
        Assert.Equal(limit, successCount);
        Assert.Equal(limit, host.AttachedCount);
    }

    // ── ActiveChannelsChanged callback ───────────────────────────────

    [Fact]
    public async Task TryAttachAsync_Successful_InvokesActiveChannelsChanged()
    {
        var (host, _) = Build();
        var callCount = 0;
        host.ActiveChannelsChanged = () =>
        {
            callCount++;
            return Task.CompletedTask;
        };

        await host.TryAttachAsync(MakeChannel(), CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task DetachAsync_RemovesChannel_InvokesActiveChannelsChanged()
    {
        var (host, _) = Build();
        var channel = MakeChannel();
        await host.TryAttachAsync(channel, CancellationToken.None);

        var callCount = 0;
        host.ActiveChannelsChanged = () =>
        {
            callCount++;
            return Task.CompletedTask;
        };

        await host.DetachAsync(channel);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TryAttachAsync_CallbackThrows_DoesNotFailAttach()
    {
        var (host, _) = Build();
        host.ActiveChannelsChanged = () => throw new InvalidOperationException("push failed");

        var result = await host.TryAttachAsync(MakeChannel(), CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task DetachAsync_CallbackThrows_DoesNotFailDetach()
    {
        var (host, manager) = Build();
        var channel = MakeChannel();
        await host.TryAttachAsync(channel, CancellationToken.None);
        host.ActiveChannelsChanged = () => throw new InvalidOperationException("push failed");

        await host.DetachAsync(channel);

        Assert.False(manager.TryGetChannel("plugin:terminal:default", out _));
    }

    [Fact]
    public async Task DetachAllAsync_InvokesActiveChannelsChangedOnce()
    {
        var (host, _) = Build();
        await host.TryAttachAsync(MakeChannel("a", "1"), CancellationToken.None);
        await host.TryAttachAsync(MakeChannel("b", "2"), CancellationToken.None);

        var callCount = 0;
        host.ActiveChannelsChanged = () =>
        {
            callCount++;
            return Task.CompletedTask;
        };

        await host.DetachAllAsync("test");

        // Exactly one push regardless of how many connectors were attached: flipping the master
        // switch off with many connectors must not produce a burst of redundant hub calls.
        Assert.Equal(1, callCount);
        Assert.Empty(host.GetAttachedChannels());
    }

    [Fact]
    public async Task DetachAllAsync_NothingAttached_DoesNotInvokeActiveChannelsChanged()
    {
        var (host, _) = Build();

        var callCount = 0;
        host.ActiveChannelsChanged = () =>
        {
            callCount++;
            return Task.CompletedTask;
        };

        await host.DetachAllAsync("test");

        Assert.Equal(0, callCount);
    }
}
