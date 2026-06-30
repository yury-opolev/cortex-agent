using System.Net.WebSockets;
using System.Text;

namespace Cortex.Contained.Channels.CloudMessaging.Transport;

/// <summary>
/// Production <see cref="ICloudTransport"/> backed by a plain
/// <see cref="ClientWebSocket"/>. The Web PubSub client URL carries an embedded
/// access token, so no separate auth header is required on the WebSocket upgrade.
/// TLS is enforced because the URL from negotiate-bridge is always wss://.
/// </summary>
public sealed class WebSocketCloudTransport : ICloudTransport
{
    private ClientWebSocket? socket;
    private readonly int receiveBufferSize;

    public WebSocketCloudTransport(int receiveBufferSize = 64 * 1024)
    {
        this.receiveBufferSize = receiveBufferSize;
    }

    /// <inheritdoc />
    public bool IsConnected
        => this.socket?.State == WebSocketState.Open;

    /// <inheritdoc />
    public async Task ConnectAsync(string url, CancellationToken ct = default)
    {
        // Dispose any stale socket before creating a fresh one
        if (this.socket is not null)
        {
            this.socket.Dispose();
        }

        this.socket = new ClientWebSocket();
        await this.socket.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendTextAsync(string json, CancellationToken ct = default)
    {
        if (this.socket is null || this.socket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("[cloud-msg] Cannot send: transport is not connected.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        await this.socket
            .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveTextAsync(CancellationToken ct = default)
    {
        if (this.socket is null)
        {
            return null;
        }

        var buffer = new byte[this.receiveBufferSize];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await this.socket
                .ReceiveAsync(buffer, ct)
                .ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (this.socket is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                await this.socket
                    .CloseAsync(WebSocketCloseStatus.NormalClosure, "channel disconnecting", ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closing was cancelled — acceptable during shutdown
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        this.socket?.Dispose();
        this.socket = null;
        return ValueTask.CompletedTask;
    }
}
