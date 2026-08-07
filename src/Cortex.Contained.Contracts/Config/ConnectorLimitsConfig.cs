namespace Cortex.Contained.Contracts.Config;

/// <summary>Per-connector frame and rate limits.</summary>
public sealed class ConnectorLimitsConfig
{
    /// <summary>Maximum size of a single WebSocket frame in bytes.</summary>
    public int MaxFrameBytes { get; set; } = 1_048_576;

    /// <summary>Maximum number of inbound messages per minute per connector.</summary>
    public int MaxMessagesPerMinute { get; set; } = 120;
}
