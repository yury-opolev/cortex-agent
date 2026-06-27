using Cortex.Contained.Contracts.Messages;

namespace Cortex.Contained.Contracts.Channels;

/// <summary>
/// Represents a bidirectional messaging channel (Web, Discord, Teams, etc.).
/// Each channel instance manages one connection to one messaging surface.
/// </summary>
public interface IChannel : IAsyncDisposable
{
    /// <summary>Unique identifier for this channel instance.</summary>
    string ChannelId { get; }

    /// <summary>The type of this channel (WebChat, Discord, Teams, etc.).</summary>
    ChannelType Type { get; }

    /// <summary>Current connection status.</summary>
    ChannelStatus Status { get; }

    /// <summary>What this channel supports.</summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>Connect to the messaging service.</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>Gracefully disconnect from the messaging service.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>Send a message through this channel.</summary>
    Task<SendResult> SendMessageAsync(OutboundMessage message, CancellationToken ct = default);

    /// <summary>Raised when an inbound message is received.</summary>
    event Func<InboundMessage, Task>? MessageReceived;

    /// <summary>Raised when the channel's connection status changes.</summary>
    event Func<ChannelStatusChange, Task>? StatusChanged;
}
