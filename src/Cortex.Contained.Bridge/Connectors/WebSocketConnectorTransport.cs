using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// WebSocket-backed implementation of <see cref="IConnectorTransport"/>.
/// </summary>
public sealed class WebSocketConnectorTransport : IConnectorTransport
{
    private readonly WebSocket socket;
    private readonly int maxFrameBytes;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    /// <inheritdoc/>
    public string RemoteEndpoint { get; }

    /// <inheritdoc/>
    public bool IsOpen => this.socket.State == WebSocketState.Open;

    /// <summary>
    /// Initialises a new <see cref="WebSocketConnectorTransport"/>.
    /// </summary>
    /// <param name="socket">The accepted WebSocket connection.</param>
    /// <param name="remoteEndpoint">Remote peer address for diagnostics.</param>
    /// <param name="maxFrameBytes">
    /// Maximum accumulated frame size in bytes. Frames exceeding this limit
    /// cause <see cref="ConnectorFrameTooLargeException"/> to be thrown from
    /// <see cref="ReceiveAsync"/>.
    /// </param>
    public WebSocketConnectorTransport(WebSocket socket, string remoteEndpoint, int maxFrameBytes)
    {
        this.socket = socket;
        this.RemoteEndpoint = remoteEndpoint;
        this.maxFrameBytes = maxFrameBytes;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Concurrent sends on a <see cref="WebSocket"/> corrupt the stream; this
    /// method serialises them with a <see cref="SemaphoreSlim"/>.
    /// <para>
    /// The frame cap is enforced on the way OUT as well as the way in. Every other size
    /// invariant in the connector surface is validated at its boundary, and an oversized
    /// outbound frame has no defined behaviour — a third-party WebSocket library may accept it,
    /// reject it, or hard-close the socket. Failing here turns an upstream budgeting mistake
    /// into a loud, local, testable error instead of a mystery disconnect in someone else's
    /// process.
    /// </para>
    /// </remarks>
    /// <exception cref="ConnectorFrameTooLargeException">
    /// The encoded frame exceeds the configured maximum.
    /// </exception>
    public async Task SendAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > this.maxFrameBytes)
        {
            throw new ConnectorFrameTooLargeException(this.maxFrameBytes);
        }

        await this.sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await this.socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct).ConfigureAwait(false);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Accumulates continuation frames into a <see cref="MemoryStream"/> until
    /// <c>EndOfMessage</c>. Uses a rented 8 192-byte chunk from
    /// <see cref="ArrayPool{T}.Shared"/>; the chunk is always returned in a
    /// <c>finally</c> block.
    /// </remarks>
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var rentedChunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var accumulator = new MemoryStream();

            while (true)
            {
                var result = await this.socket.ReceiveAsync(
                    new ArraySegment<byte>(rentedChunk),
                    ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    throw new InvalidOperationException("connector frames must be text");
                }

                var newSize = accumulator.Length + result.Count;
                if (newSize > this.maxFrameBytes)
                {
                    throw new ConnectorFrameTooLargeException(this.maxFrameBytes);
                }

                accumulator.Write(rentedChunk, 0, result.Count);

                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedChunk);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The WebSocket close-reason field is limited to 123 bytes (RFC 6455 §5.5.1).
    /// This method silently truncates longer reasons. Exceptions from a vanished
    /// peer (<see cref="WebSocketException"/>, <see cref="ObjectDisposedException"/>,
    /// <see cref="OperationCanceledException"/>) are swallowed so teardown never
    /// throws for network errors.
    /// </remarks>
    public async Task CloseAsync(string reason, CancellationToken ct)
    {
        const int maxReasonBytes = 123;
        reason = TruncateToUtf8ByteLimit(reason, maxReasonBytes);

        try
        {
            await this.socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, ct).ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        this.sendLock.Dispose();
        this.socket.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Truncates <paramref name="s"/> so that its UTF-8 representation fits within
    /// <paramref name="maxBytes"/> bytes, without splitting a multi-byte sequence
    /// or a UTF-16 surrogate pair.
    /// </summary>
    internal static string TruncateToUtf8ByteLimit(string s, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes)
        {
            return s;
        }

        // Walk backwards from the end until the encoded byte count fits.
        var charCount = s.Length;
        while (charCount > 0 && Encoding.UTF8.GetByteCount(s.AsSpan(0, charCount)) > maxBytes)
        {
            charCount--;
        }

        // Never cut between the halves of a surrogate pair: an orphaned high surrogate
        // encodes as the U+FFFD replacement character and corrupts the reason string.
        if (charCount > 0 && char.IsHighSurrogate(s[charCount - 1]))
        {
            charCount--;
        }

        return s[..charCount];
    }
}
