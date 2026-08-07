using Cortex.Contained.Bridge.Channels;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Singleton that manages the set of active plugin channel registrations.
/// Implements <see cref="IConnectorRegistry"/> so <see cref="ConnectorSession"/>
/// instances can attach and detach without a direct dependency on
/// <see cref="ChannelManager"/>.
/// </summary>
/// <remarks>
/// The codebase uses <see cref="BridgeConfig"/> injected directly (as a singleton
/// resolved from <c>IOptions&lt;BridgeConfig&gt;.Value</c> at startup) rather than
/// <c>IOptionsMonitor&lt;BridgeConfig&gt;</c>. <see cref="ConnectorHost"/> follows
/// the same pattern and receives <see cref="BridgeConfig"/> rather than the options
/// monitor.
/// </remarks>
public sealed partial class ConnectorHost : IConnectorRegistry
{
    private readonly ChannelManager channelManager;
    private readonly BridgeConfig config;
    private readonly ILogger<ConnectorHost> logger;
    private readonly Dictionary<string, PluginChannel> channels = new(StringComparer.Ordinal);
    private readonly Lock syncLock = new();

    /// <summary>
    /// Initialises a new <see cref="ConnectorHost"/>.
    /// </summary>
    public ConnectorHost(ChannelManager channelManager, BridgeConfig config, ILogger<ConnectorHost> logger)
    {
        this.channelManager = channelManager;
        this.config = config;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async ValueTask<ConnectorAttachResult> TryAttachAsync(PluginChannel channel, CancellationToken ct)
    {
        var settings = this.config.Connectors;

        if (!settings.Enabled)
        {
            return ConnectorAttachResult.Failed(ConnectorErrorCodes.Disabled, "Connector subsystem is disabled.");
        }

        lock (this.syncLock)
        {
            if (this.channels.ContainsKey(channel.ChannelId))
            {
                return ConnectorAttachResult.Failed(
                    ConnectorErrorCodes.Duplicate,
                    $"A connector with channel id '{channel.ChannelId}' is already attached.");
            }

            if (this.channels.Count >= settings.MaxConnectors)
            {
                return ConnectorAttachResult.Failed(
                    ConnectorErrorCodes.ConnectorLimitReached,
                    $"Maximum number of connectors ({settings.MaxConnectors}) has been reached.");
            }

            this.channels[channel.ChannelId] = channel;
        }

        try
        {
            this.channelManager.RegisterChannel(channel);
            await channel.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Roll the registration back, otherwise the slot is permanently poisoned and
            // every later attach for this channel id is rejected as a duplicate.
            lock (this.syncLock)
            {
                if (this.channels.TryGetValue(channel.ChannelId, out var stored) && ReferenceEquals(stored, channel))
                {
                    this.channels.Remove(channel.ChannelId);
                }
            }

            this.channelManager.UnregisterChannel(channel.ChannelId);
            this.LogAttachFailed(channel.ChannelId, ex.Message);

            if (ex is OperationCanceledException)
            {
                throw;
            }

            return ConnectorAttachResult.Failed(ConnectorErrorCodes.ProtocolViolation, "Connector channel could not be attached.");
        }

        this.LogChannelAttached(channel.ChannelId);
        return ConnectorAttachResult.Ok();
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The channel is always disconnected, but it is only removed from the registry and
    /// from <see cref="ChannelManager"/> when the stored instance is the <em>same
    /// reference</em> as <paramref name="channel"/>. That guards against a stale session's
    /// teardown evicting a newer session's channel when two sessions race for the same
    /// channel id, while still guaranteeing the stale channel itself is shut down.
    /// </remarks>
    public async ValueTask DetachAsync(PluginChannel channel)
    {
        bool removed;
        lock (this.syncLock)
        {
            removed = this.channels.TryGetValue(channel.ChannelId, out var existing)
                && ReferenceEquals(existing, channel)
                && this.channels.Remove(channel.ChannelId);
        }

        if (removed)
        {
            this.channelManager.UnregisterChannel(channel.ChannelId);
            this.LogChannelDetached(channel.ChannelId);
        }

        await channel.DisconnectAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> DetachByChannelIdAsync(string channelId)
    {
        PluginChannel? channel;
        lock (this.syncLock)
        {
            this.channels.TryGetValue(channelId, out channel);
        }

        if (channel is null)
        {
            return false;
        }

        await this.DetachAsync(channel).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Returns a snapshot of all currently attached plugin channels.
    /// Used by the Web UI in Phase 5 to display the connector list.
    /// </summary>
    public IReadOnlyList<PluginChannel> GetAttachedChannels()
    {
        lock (this.syncLock)
        {
            return [.. this.channels.Values];
        }
    }

    /// <summary>Number of currently attached plugin channels.</summary>
    public int AttachedCount
    {
        get
        {
            lock (this.syncLock)
            {
                return this.channels.Count;
            }
        }
    }

    /// <summary>
    /// Detaches every attached channel. Used by the master kill-switch.
    /// </summary>
    public async Task DetachAllAsync(string reason)
    {
        List<PluginChannel> snapshot;
        lock (this.syncLock)
        {
            snapshot = [.. this.channels.Values];
        }

        foreach (var channel in snapshot)
        {
            await this.DetachAsync(channel).ConfigureAwait(false);
        }

        this.LogAllDetached(reason);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connector channel attached: {ChannelId}")]
    private partial void LogChannelAttached(string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connector channel attach failed and was rolled back: {ChannelId}, error={ErrorMessage}")]
    private partial void LogAttachFailed(string channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connector channel detached: {ChannelId}")]
    private partial void LogChannelDetached(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "All connector channels detached. Reason: {Reason}")]
    private partial void LogAllDetached(string reason);
}
