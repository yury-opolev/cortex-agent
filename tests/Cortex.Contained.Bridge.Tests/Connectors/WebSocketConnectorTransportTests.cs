using System.Net.WebSockets;
using System.Text;
using Cortex.Contained.Bridge.Connectors;

namespace Cortex.Contained.Bridge.Tests.Connectors;

public sealed class WebSocketConnectorTransportTests
{
    // ── Close-reason truncation ──────────────────────────────────────

    [Fact]
    public void TruncateToUtf8ByteLimit_ShortAscii_Unchanged()
    {
        var input = "hello world";
        var result = WebSocketConnectorTransport.TruncateToUtf8ByteLimit(input, 123);
        Assert.Equal(input, result);
    }

    [Fact]
    public void TruncateToUtf8ByteLimit_ExactLimit_Unchanged()
    {
        var input = new string('a', 123);
        var result = WebSocketConnectorTransport.TruncateToUtf8ByteLimit(input, 123);
        Assert.Equal(input, result);
    }

    [Fact]
    public void TruncateToUtf8ByteLimit_TooLong_Truncated()
    {
        var input = new string('a', 200);
        var result = WebSocketConnectorTransport.TruncateToUtf8ByteLimit(input, 123);
        Assert.Equal(123, Encoding.UTF8.GetByteCount(result));
    }

    [Fact]
    public void TruncateToUtf8ByteLimit_MultibyteSafe_NoBrokenSequences()
    {
        // Each '€' is 3 bytes in UTF-8.
        // 41 × 3 = 123 bytes exactly; 42 × 3 = 126 bytes → must trim to 41 chars.
        var input = new string('€', 42);
        var result = WebSocketConnectorTransport.TruncateToUtf8ByteLimit(input, 123);
        var byteCount = Encoding.UTF8.GetByteCount(result);
        Assert.True(byteCount <= 123, $"byte count {byteCount} exceeds 123");
        // Ensure no broken sequences by round-tripping through the decoder.
        var bytes = Encoding.UTF8.GetBytes(result);
        var decoded = Encoding.UTF8.GetString(bytes);
        Assert.Equal(result, decoded);
    }

    [Fact]
    public void TruncateToUtf8ByteLimit_SurrogatePairAtBoundary_DoesNotSplitPair()
    {
        // '🎉' is U+1F389: a UTF-16 surrogate pair and 4 UTF-8 bytes.
        // 31 × 4 = 124 bytes, one over the 123-byte close-reason limit, so the last
        // emoji must be dropped whole. Trimming a single char would leave an orphaned
        // high surrogate that encodes as U+FFFD.
        var input = string.Concat(Enumerable.Repeat("🎉", 31));
        var result = WebSocketConnectorTransport.TruncateToUtf8ByteLimit(input, 123);

        Assert.True(Encoding.UTF8.GetByteCount(result) <= 123);
        Assert.Equal(60, result.Length);
        Assert.Equal(result, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(result)));
    }

    // ── ConnectorFrameTooLargeException ──────────────────────────────

    [Fact]
    public void ConnectorFrameTooLargeException_MaxFrameBytes_StoredCorrectly()
    {
        var ex = new ConnectorFrameTooLargeException(65536);
        Assert.Equal(65536, ex.MaxFrameBytes);
        Assert.Contains("65536", ex.Message);
    }

    // ── Outbound frame cap ───────────────────────────────────────────

    [Fact]
    public async Task SendAsync_FrameWithinTheLimit_IsSent()
    {
        var (transport, socket) = BuildTransport(maxFrameBytes: 1024);

        await transport.SendAsync(new string('a', 512), CancellationToken.None);

        Assert.Single(socket.Sent);
    }

    [Fact]
    public async Task SendAsync_FrameExceedingTheLimit_ThrowsInsteadOfSending()
    {
        // The frame cap used to be enforced only on RECEIVE. An oversized outbound frame has no
        // defined behaviour in a third-party WebSocket library, so this backstop turns an
        // upstream budgeting bug into a loud local failure instead of a mystery disconnect.
        var (transport, socket) = BuildTransport(maxFrameBytes: 1024);

        var ex = await Assert.ThrowsAsync<ConnectorFrameTooLargeException>(
            () => transport.SendAsync(new string('a', 2048), CancellationToken.None));

        Assert.Equal(1024, ex.MaxFrameBytes);
        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task SendAsync_CountsUtf8BytesNotCharacters()
    {
        // 400 euro signs is 400 UTF-16 units but 1200 UTF-8 bytes. Counting characters would
        // let a frame through that is comfortably over the wire limit.
        var (transport, socket) = BuildTransport(maxFrameBytes: 1024);

        await Assert.ThrowsAsync<ConnectorFrameTooLargeException>(
            () => transport.SendAsync(new string('€', 400), CancellationToken.None));

        Assert.Empty(socket.Sent);
    }

    private static (WebSocketConnectorTransport Transport, RecordingWebSocket Socket) BuildTransport(int maxFrameBytes)
    {
        var socket = new RecordingWebSocket();
        return (new WebSocketConnectorTransport(socket, "127.0.0.1", maxFrameBytes), socket);
    }

    /// <summary>
    /// Minimal <see cref="WebSocket"/> that records what was sent. Only the members the
    /// transport's send path touches are implemented.
    /// </summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        public List<byte[]> Sent { get; } = [];

        public override WebSocketState State => WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            this.Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public override void Abort()
        {
        }

        public override void Dispose()
        {
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    // Note: Integration tests with real WebSocket pairs require a live HTTP server.
    // The pure helpers above cover the testable pure logic; the transport integration
    // is covered indirectly by ConnectorSessionTests via FakeConnectorTransport.
}
