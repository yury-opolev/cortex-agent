using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Bridge.Channels;

/// <summary>
/// Manages the lifecycle of all <see cref="IChannel"/> instances:
/// registration, connection, disconnection, message routing, and disposal.
/// </summary>
public sealed partial class ChannelManager : IAsyncDisposable
{
    private readonly Dictionary<string, IChannel> channels = new(StringComparer.Ordinal);

    /// <summary>
    /// Additional routing IDs that map to an already-registered primary channel.
    /// Used when a single <see cref="IChannel"/> implementation services multiple
    /// logical sub-channels (e.g. <c>DiscordChannel</c> handles "discord-dm",
    /// "discord-voice", etc.). Lookups via <see cref="TryGetChannel"/> fall through
    /// to this dictionary after the primary lookup misses. Iterations
    /// (<c>GetAllChannels</c>, <see cref="ConnectAllAsync"/>, etc.) only touch the
    /// primary dictionary so each channel is still connected / disposed once.
    /// </summary>
    private readonly Dictionary<string, IChannel> channelAliases = new(StringComparer.Ordinal);
    private readonly Lock syncLock = new();
    private readonly ILogger<ChannelManager> logger;

    /// <summary>Raised when any registered channel receives an inbound message.</summary>
    public event Func<IChannel, InboundMessage, Task>? MessageReceived;

    /// <summary>Raised when any registered channel changes connection status.</summary>
    public event Func<IChannel, ChannelStatusChange, Task>? ChannelStatusChanged;

    public ChannelManager(ILogger<ChannelManager> logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Register a channel and subscribe to its events.
    /// </summary>
    public void RegisterChannel(IChannel channel)
    {
        lock (this.syncLock)
        {
            this.channels[channel.ChannelId] = channel;
        }

        channel.MessageReceived += message => OnChannelMessageReceived(channel, message);
        channel.StatusChanged += change => OnChannelStatusChanged(channel, change);

        this.LogChannelRegistered(channel.ChannelId, channel.Type);
    }

    /// <summary>
    /// Register an additional routing ID (<paramref name="alias"/>) that resolves to
    /// an already-registered channel. Used when one <see cref="IChannel"/> implementation
    /// services multiple logical channel IDs (e.g. Discord DM, guild text, voice).
    /// Events are not re-subscribed — they are already hooked by the primary
    /// <see cref="RegisterChannel(IChannel)"/> call, and aliases are excluded from
    /// iteration so lifecycle callbacks fire exactly once.
    /// </summary>
    public void RegisterChannelAlias(string alias, IChannel channel)
    {
        lock (this.syncLock)
        {
            this.channelAliases[alias] = channel;
        }

        this.LogChannelAliasRegistered(alias, channel.ChannelId);
    }

    /// <summary>
    /// Connect all registered channels.
    /// </summary>
    public async Task ConnectAllAsync(CancellationToken ct)
    {
        List<IChannel> snapshot;
        lock (this.syncLock)
        {
            snapshot = [.. this.channels.Values];
        }

        foreach (var channel in snapshot)
        {
            this.LogChannelConnecting(channel.ChannelId);

            try
            {
                await channel.ConnectAsync(ct).ConfigureAwait(false);
                this.LogChannelConnected(channel.ChannelId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogChannelConnectFailed(channel.ChannelId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Disconnect all registered channels.
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        List<IChannel> snapshot;
        lock (this.syncLock)
        {
            snapshot = [.. this.channels.Values];
        }

        foreach (var channel in snapshot)
        {
            try
            {
                await channel.DisconnectAsync().ConfigureAwait(false);
                this.LogChannelDisconnected(channel.ChannelId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogChannelConnectFailed(channel.ChannelId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Look up a channel by its unique identifier. Primary registrations are checked
    /// first, then aliases registered via <see cref="RegisterChannelAlias"/>.
    /// </summary>
    public bool TryGetChannel(string channelId, out IChannel? channel)
    {
        lock (this.syncLock)
        {
            return this.channels.TryGetValue(channelId, out channel)
                || this.channelAliases.TryGetValue(channelId, out channel);
        }
    }

    /// <summary>
    /// Get all channels of a specific type.
    /// </summary>
    public IReadOnlyList<IChannel> GetChannelsByType(ChannelType type)
    {
        lock (this.syncLock)
        {
            return [.. this.channels.Values.Where(channel => channel.Type == type)];
        }
    }

    /// <summary>
    /// Get all registered channels.
    /// </summary>
    public IReadOnlyList<IChannel> GetAllChannels()
    {
        lock (this.syncLock)
        {
            return [.. this.channels.Values];
        }
    }

    private Task OnChannelMessageReceived(IChannel channel, InboundMessage message)
    {
        this.LogMessageReceived(channel.ChannelId, message.MessageId);
        return MessageReceived?.Invoke(channel, message) ?? Task.CompletedTask;
    }

    private Task OnChannelStatusChanged(IChannel channel, ChannelStatusChange change)
    {
        this.LogChannelStatusChanged(channel.ChannelId, change.PreviousStatus, change.CurrentStatus);
        return ChannelStatusChanged?.Invoke(channel, change) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<IChannel> snapshot;
        lock (this.syncLock)
        {
            snapshot = [.. this.channels.Values];
            this.channels.Clear();
        }

        foreach (var channel in snapshot)
        {
            try
            {
                await channel.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogChannelConnectFailed(channel.ChannelId, ex.Message);
            }

            try
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                this.LogChannelConnectFailed(channel.ChannelId, ex.Message);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel registered: {ChannelId} (type={ChannelType})")]
    private partial void LogChannelRegistered(string channelId, ChannelType channelType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel alias registered: {Alias} -> {PrimaryChannelId}")]
    private partial void LogChannelAliasRegistered(string alias, string primaryChannelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel connecting: {ChannelId}")]
    private partial void LogChannelConnecting(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel connected: {ChannelId}")]
    private partial void LogChannelConnected(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel disconnected: {ChannelId}")]
    private partial void LogChannelDisconnected(string channelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Channel connect failed: {ChannelId}, error={ErrorMessage}")]
    private partial void LogChannelConnectFailed(string channelId, string errorMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "Channel status changed: {ChannelId}, {PreviousStatus} -> {CurrentStatus}")]
    private partial void LogChannelStatusChanged(string channelId, ChannelStatus previousStatus, ChannelStatus currentStatus);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Message received: channelId={ChannelId}, messageId={MessageId}")]
    private partial void LogMessageReceived(string channelId, string messageId);
}
