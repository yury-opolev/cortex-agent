namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Thrown by <see cref="WebSocketConnectorTransport"/> when an incoming frame
/// accumulates more bytes than the configured <see cref="MaxFrameBytes"/> limit.
/// The owning session translates this into an <c>error</c> frame + close.
/// </summary>
public sealed class ConnectorFrameTooLargeException : Exception
{
    /// <summary>Configured maximum frame size in bytes.</summary>
    public int MaxFrameBytes { get; }

    /// <summary>Initialises the exception with the configured limit.</summary>
    public ConnectorFrameTooLargeException(int maxFrameBytes)
        : base($"Connector frame exceeds the maximum allowed size of {maxFrameBytes} bytes.")
    {
        this.MaxFrameBytes = maxFrameBytes;
    }

    /// <summary>Initialises the exception with the configured limit and a custom message.</summary>
    public ConnectorFrameTooLargeException(int maxFrameBytes, string message)
        : base(message)
    {
        this.MaxFrameBytes = maxFrameBytes;
    }
}
