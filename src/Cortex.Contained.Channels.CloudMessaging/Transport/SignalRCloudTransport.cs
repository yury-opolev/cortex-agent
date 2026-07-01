using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.CloudMessaging.Transport;

/// <summary>
/// Production <see cref="ICloudTransport"/> backed by a <see cref="HubConnection"/>.
/// Inbound "receive" frames pushed by the hub are queued in an in-process channel so
/// <see cref="ReceiveTextAsync"/> can pull them one at a time (bridging SignalR push
/// to the pull-based <see cref="ICloudTransport"/> interface).
/// The access token is forwarded via <c>options.AccessTokenProvider</c> so the SignalR
/// WebSocket upgrade carries it as <c>?access_token=...</c> (per SignalR convention).
/// </summary>
public sealed partial class SignalRCloudTransport : ICloudTransport
{
    /// <summary>SignalR hub method name for inbound frames pushed by the server.</summary>
    private const string ReceiveMethod = "receive";

    /// <summary>SignalR hub method name for outbound frames from this bridge.</summary>
    private const string SendFrameMethod = "SendFrame";

    private readonly Func<Task<string?>> accessTokenProvider;
    private readonly ILogger logger;

    private HubConnection? connection;

    // Bounded channel; if the consumer falls behind we drop the oldest frame rather than OOM.
    private readonly Channel<string?> receiveQueue =
        System.Threading.Channels.Channel.CreateBounded<string?>(
            new BoundedChannelOptions(capacity: 1_024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });

    private IDisposable? receiveRegistration;

    /// <param name="accessTokenProvider">
    /// Async delegate that returns the current S2S bearer token.
    /// Called by the HubConnection at connect time and on reconnect.
    /// </param>
    /// <param name="logger">Logger (telemetry).</param>
    public SignalRCloudTransport(Func<Task<string?>> accessTokenProvider, ILogger logger)
    {
        this.accessTokenProvider = accessTokenProvider ?? throw new ArgumentNullException(nameof(accessTokenProvider));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsConnected =>
        this.connection?.State == HubConnectionState.Connected;

    /// <inheritdoc />
    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        // Dispose any stale connection before creating a new one.
        await this.DisposeConnectionAsync().ConfigureAwait(false);

        // Drain the receive queue from the old connection so no stale frames leak.
        while (this.receiveQueue.Reader.TryRead(out _))
        {
            // discard
        }

        this.connection = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.AccessTokenProvider = this.accessTokenProvider;
            })
            .WithAutomaticReconnect()
            .Build();

        // Wire the inbound handler: hub pushes "receive" frames → enqueue them.
        this.receiveRegistration = this.connection.On<string>(ReceiveMethod, frame =>
        {
            // TryWrite will succeed unless the channel is at capacity and DropOldest is dropping.
            this.receiveQueue.Writer.TryWrite(frame);
        });

        // Enqueue a null sentinel when the connection closes so ReceiveTextAsync returns.
        this.connection.Closed += _ =>
        {
            this.receiveQueue.Writer.TryWrite(null);
            return Task.CompletedTask;
        };

        await this.connection.StartAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendTextAsync(string json, CancellationToken ct = default)
    {
        if (this.connection is null || this.connection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException("[cloud-msg] Cannot send: transport is not connected.");
        }

        return this.connection.InvokeAsync(SendFrameMethod, json, ct);
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveTextAsync(CancellationToken ct = default)
    {
        try
        {
            return await this.receiveQueue.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (this.connection is { State: HubConnectionState.Connected or HubConnectionState.Reconnecting })
        {
            try
            {
                await this.connection.StopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closing was cancelled — acceptable during shutdown.
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await this.DisposeConnectionAsync().ConfigureAwait(false);
        this.receiveQueue.Writer.TryComplete();
    }

    private async Task DisposeConnectionAsync()
    {
        this.receiveRegistration?.Dispose();
        this.receiveRegistration = null;

        if (this.connection is not null)
        {
            try
            {
                await this.connection.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Broad catch: best-effort cleanup
            catch (Exception ex)
            {
                LogDisposeException(this.logger, ex);
            }
#pragma warning restore CA1031
            finally
            {
                this.connection = null;
            }
        }
    }

    // ── Source-generated LoggerMessage ────────────────────────────────

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "[cloud-msg] Exception while disposing HubConnection (ignored).")]
    private static partial void LogDisposeException(ILogger logger, Exception ex);
}
