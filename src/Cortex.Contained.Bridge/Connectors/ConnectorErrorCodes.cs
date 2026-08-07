namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Well-known error codes sent in connector error frames.</summary>
public static class ConnectorErrorCodes
{
    /// <summary>The received frame could not be parsed as valid JSON.</summary>
    public const string MalformedFrame = "malformed_frame";

    /// <summary>The frame type is valid JSON but not a recognised frame type.</summary>
    public const string UnknownFrameType = "unknown_frame_type";

    /// <summary>The frame type is known but the payload failed validation.</summary>
    public const string InvalidPayload = "invalid_payload";

    /// <summary>The connector has violated the protocol state machine.</summary>
    public const string ProtocolViolation = "protocol_violation";

    /// <summary>The frame exceeds the configured maximum frame size.</summary>
    public const string FrameTooLarge = "frame_too_large";

    /// <summary>The message content exceeds the negotiated maximum message length.</summary>
    public const string MessageTooLong = "message_too_long";

    /// <summary>The connector has exceeded its message rate limit.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The connector attempted an operation that requires prior pairing.</summary>
    public const string NotPaired = "not_paired";

    /// <summary>The maximum number of concurrent connectors has been reached.</summary>
    public const string ConnectorLimitReached = "connector_limit_reached";

    /// <summary>A connector with the same key+instanceId is already attached.</summary>
    public const string Duplicate = "duplicate_connector";

    /// <summary>The connector subsystem is disabled by configuration.</summary>
    public const string Disabled = "connectors_disabled";
}
