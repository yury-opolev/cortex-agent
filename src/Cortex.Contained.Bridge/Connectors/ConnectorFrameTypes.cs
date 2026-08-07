namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Well-known frame type names used on the connector wire protocol.</summary>
public static class ConnectorFrameTypes
{
    /// <summary>Connector attaches, declares identity and capabilities.</summary>
    public const string Hello = "hello";

    /// <summary>Connector sends a user message.</summary>
    public const string Inbound = "inbound";

    /// <summary>Connector cancels an in-flight generation.</summary>
    public const string Abort = "abort";

    /// <summary>Connector responds to a ping.</summary>
    public const string Pong = "pong";

    /// <summary>Bridge requests connector pairing.</summary>
    public const string PairingRequired = "pairing_required";

    /// <summary>Bridge confirms successful pairing.</summary>
    public const string Paired = "paired";

    /// <summary>Bridge denies a pairing attempt.</summary>
    public const string PairingDenied = "pairing_denied";

    /// <summary>Bridge accepts the attach and enters steady state.</summary>
    public const string Ready = "ready";

    /// <summary>Bridge sends a typing indicator.</summary>
    public const string Typing = "typing";

    /// <summary>Bridge sends a streaming text chunk.</summary>
    public const string Stream = "stream";

    /// <summary>Bridge sends the final agent response.</summary>
    public const string Outbound = "outbound";

    /// <summary>Bridge reports a protocol or policy error.</summary>
    public const string Error = "error";

    /// <summary>Bridge probes connector liveness.</summary>
    public const string Ping = "ping";
}
