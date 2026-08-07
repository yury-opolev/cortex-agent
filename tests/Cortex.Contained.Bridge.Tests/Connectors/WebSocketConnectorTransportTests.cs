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

    // Note: Integration tests with real WebSocket pairs require a live HTTP server.
    // The pure helpers above cover the testable pure logic; the transport integration
    // is covered indirectly by ConnectorSessionTests via FakeConnectorTransport.
}
