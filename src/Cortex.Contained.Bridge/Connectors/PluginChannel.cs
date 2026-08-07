using Cortex.Contained.Contracts.Channels;
using Cortex.Contained.Contracts.Messages;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// An <see cref="IChannel"/> implementation backed by an external connector process
/// communicating over the WebSocket connector protocol. Each connected
/// <c>key</c>+<c>instanceId</c> pair is represented by exactly one <see cref="PluginChannel"/>.
/// </summary>
public sealed partial class PluginChannel : IChannel
{
    private readonly ILogger<PluginChannel> logger;
    private readonly Lock statusLock = new();
    private ChannelStatus status = ChannelStatus.Disconnected;
    private Func<OutboundMessage, CancellationToken, Task<SendResult>>? outboundSink;

    /// <summary>
    /// Initialises a new <see cref="PluginChannel"/>.
    /// </summary>
    /// <param name="pluginKey">Connector type key (e.g. <c>terminal</c>).</param>
    /// <param name="instanceId">Connector instance identifier (e.g. <c>default</c>).</param>
    /// <param name="capabilities">Capabilities negotiated during the <c>hello</c> handshake.</param>
    /// <param name="displayName">Human-readable name advertised by the connector.</param>
    /// <param name="logger">Logger instance.</param>
    public PluginChannel(
        string pluginKey,
        string instanceId,
        ChannelCapabilities capabilities,
        string displayName,
        ILogger<PluginChannel> logger)
    {
        this.logger = logger;
        this.PluginKey = pluginKey;
        this.InstanceId = instanceId;
        this.Capabilities = capabilities;
        this.DisplayName = displayName;
        this.ChannelId = ConnectorChannelId.Create(pluginKey, instanceId);
    }

    // ── IChannel properties ──────────────────────────────────────────

    /// <inheritdoc />
    public string ChannelId { get; }

    /// <summary>Connector type key.</summary>
    public string PluginKey { get; }

    /// <summary>Connector instance identifier.</summary>
    public string InstanceId { get; }

    /// <summary>
    /// Human-readable name advertised by the connector. This is untrusted input and
    /// must be escaped wherever it is rendered.
    /// </summary>
    public string DisplayName { get; }

    /// <inheritdoc />
    public ChannelType Type => ChannelType.Plugin;

    /// <inheritdoc />
    public ChannelStatus Status
    {
        get
        {
            lock (this.statusLock)
            {
                return this.status;
            }
        }
    }

    /// <inheritdoc />
    public ChannelCapabilities Capabilities { get; }

    // ── IChannel events ──────────────────────────────────────────────

    /// <inheritdoc />
    public event Func<InboundMessage, Task>? MessageReceived;

    /// <inheritdoc />
    public event Func<ChannelStatusChange, Task>? StatusChanged;

    // ── Outbound sink ────────────────────────────────────────────────

    /// <summary>
    /// Set by the owning connector session to deliver outbound messages over the socket,
    /// which avoids a circular constructor dependency between the session and the channel.
    /// When null, <see cref="SendMessageAsync"/> returns an error result rather than throwing.
    /// </summary>
    /// <remarks>
    /// The session sets this on its own thread while <see cref="HubMessageDispatcher"/> may
    /// read it from any thread, so access is through <see cref="Volatile"/> to guarantee the
    /// write is visible to subsequent reads.
    /// </remarks>
    public Func<OutboundMessage, CancellationToken, Task<SendResult>>? OutboundSink
    {
        get => Volatile.Read(ref this.outboundSink);
        set => Volatile.Write(ref this.outboundSink, value);
    }

    // ── IChannel methods ─────────────────────────────────────────────

    /// <inheritdoc />
    public Task ConnectAsync(CancellationToken ct = default)
    {
        this.LogConnected(this.ChannelId);
        return this.SetStatusAsync(ChannelStatus.Connected, "Connector attached");
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        this.LogDisconnected(this.ChannelId);
        return this.SetStatusAsync(ChannelStatus.Disconnected, "Connector detached");
    }

    /// <inheritdoc />
    public async Task<SendResult> SendMessageAsync(OutboundMessage message, CancellationToken ct = default)
    {
        var sink = this.OutboundSink;
        if (sink is null)
        {
            this.LogSendWithoutSink(this.ChannelId, message.MessageId);
            return SendResult.Error("connector is not attached");
        }

        return await sink(message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers an inbound message to all <see cref="MessageReceived"/> subscribers.
    /// Returns <see cref="Task.CompletedTask"/> when there are no subscribers.
    /// </summary>
    /// <param name="message">The message received from the connector.</param>
    public Task ReceiveInboundAsync(InboundMessage message)
    {
        return this.MessageReceived?.Invoke(message) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.DisconnectAsync().ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private Task SetStatusAsync(ChannelStatus newStatus, string reason)
    {
        ChannelStatus previous;
        lock (this.statusLock)
        {
            if (this.status == newStatus)
            {
                return Task.CompletedTask;
            }

            previous = this.status;
            this.status = newStatus;
        }

        var change = new ChannelStatusChange(previous, newStatus, reason);
        return this.StatusChanged?.Invoke(change) ?? Task.CompletedTask;
    }

    // ── Logging ──────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "PluginChannel connected: {ChannelId}")]
    private partial void LogConnected(string channelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "PluginChannel disconnected: {ChannelId}")]
    private partial void LogDisconnected(string channelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PluginChannel {ChannelId}: send attempted for message {MessageId} but no outbound sink is attached.")]
    private partial void LogSendWithoutSink(string channelId, string messageId);
}
