namespace Cortex.Contained.Contracts.Channels;

/// <summary>Type of messaging channel.</summary>
public enum ChannelType
{
    WebChat = 0,
    Teams = 2,
    Telegram = 3,
    Voice = 4,
    Discord = 5,
    CloudMessaging = 6,
}

/// <summary>Connection status of a channel.</summary>
public enum ChannelStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Pairing = 3,
    Reconnecting = 4,
    Error = 5
}

/// <summary>Describes the capabilities of a channel.</summary>
public sealed record ChannelCapabilities
{
    public bool SupportsMedia { get; init; }
    public bool SupportsThreads { get; init; }
    public bool SupportsReactions { get; init; }
    public bool SupportsStreaming { get; init; }
    public bool SupportsRichText { get; init; }
    public bool SupportsGroups { get; init; }
    public bool SupportsEditing { get; init; }
    public bool SupportsDeletion { get; init; }
    public int MaxMessageLength { get; init; } = int.MaxValue;
    public IReadOnlyList<string> SupportedMediaTypes { get; init; } = [];
}

/// <summary>Describes a channel status transition.</summary>
public sealed record ChannelStatusChange(
    ChannelStatus PreviousStatus,
    ChannelStatus CurrentStatus,
    string? Reason = null,
    Exception? Error = null
);
