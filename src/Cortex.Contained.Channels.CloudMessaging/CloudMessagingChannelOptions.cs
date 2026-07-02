namespace Cortex.Contained.Channels.CloudMessaging;

/// <summary>
/// Configuration options for <see cref="CloudMessagingChannel"/>.
/// Populated from cortex.yml (channels:cloud-messaging:settings).
/// </summary>
public sealed class CloudMessagingChannelOptions
{
    /// <summary>Unique channel ID used for routing.</summary>
    public string ChannelId { get; init; } = "cloud-messaging-default";

    /// <summary>AI Messenger service base URL (e.g. https://api.cortex-messenger.example.com).</summary>
    public string ServiceBaseUrl { get; init; } = string.Empty;

    /// <summary>Maximum frame size in bytes. Frames larger than this are dropped (fail-closed).</summary>
    public int MaxFrameBytes { get; init; } = 512 * 1024; // 512 KB

    /// <summary>Base delay in ms for exponential reconnect backoff.</summary>
    public int ReconnectBaseDelayMs { get; init; } = 1_000;

    /// <summary>Maximum delay in ms for reconnect backoff.</summary>
    public int ReconnectMaxDelayMs { get; init; } = 60_000;
}
