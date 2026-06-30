namespace Cortex.Contained.Channels.CloudMessaging.Transport;

/// <summary>
/// Abstraction over the Web PubSub WebSocket connection so the
/// <see cref="CloudMessagingChannel"/> is unit-testable without a live
/// connection. The real implementation uses <see cref="System.Net.WebSockets.ClientWebSocket"/>;
/// tests inject a <c>FakeCloudTransport</c>.
/// </summary>
public interface ICloudTransport : IAsyncDisposable
{
    /// <summary>
    /// Connects to the given WebSocket URL (already contains the access token).
    /// </summary>
    Task ConnectAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Sends a JSON text frame to the service.
    /// </summary>
    Task SendTextAsync(string json, CancellationToken ct = default);

    /// <summary>
    /// Receives the next text frame. Returns null when the connection closes normally.
    /// </summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct = default);

    /// <summary>
    /// Closes the WebSocket gracefully.
    /// </summary>
    Task CloseAsync(CancellationToken ct = default);

    /// <summary>Whether the transport currently reports an open connection.</summary>
    bool IsConnected { get; }
}
