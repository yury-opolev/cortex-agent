namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Transport seam that decouples the connector protocol state machine from a
/// real WebSocket. Implementations may be substituted in tests without a live
/// socket.
/// </summary>
public interface IConnectorTransport : IAsyncDisposable
{
    /// <summary>True while the transport can still send and receive.</summary>
    bool IsOpen { get; }

    /// <summary>The remote peer address, used for loopback enforcement and diagnostics.</summary>
    string RemoteEndpoint { get; }

    /// <summary>
    /// Sends one JSON text frame.
    /// </summary>
    /// <param name="json">The serialised frame.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Implementations MUST be safe to call concurrently. The connector session sends from the
    /// read loop, from the agent's outbound path, and from the heartbeat timer, and interleaved
    /// writes on a raw <c>WebSocket</c> corrupt the stream.
    /// </remarks>
    Task SendAsync(string json, CancellationToken ct);

    /// <summary>Receives the next JSON text frame, or null when the peer closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Closes the transport with a status description.</summary>
    Task CloseAsync(string reason, CancellationToken ct);
}
