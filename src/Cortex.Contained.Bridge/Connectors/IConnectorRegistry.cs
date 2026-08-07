namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Manages the set of connected plugin channels.
/// Provides the seam between <see cref="ConnectorSession"/> and
/// <see cref="ConnectorHost"/> so that session tests do not depend on
/// <c>ChannelManager</c>.
/// </summary>
public interface IConnectorRegistry
{
    /// <summary>
    /// Registers a freshly handshaken plugin channel.
    /// Returns a failure result when policy rejects it (disabled, duplicate, limit reached).
    /// </summary>
    ValueTask<ConnectorAttachResult> TryAttachAsync(PluginChannel channel, CancellationToken ct);

    /// <summary>
    /// Removes a previously attached plugin channel.
    /// Safe to call when the channel was never attached.
    /// </summary>
    ValueTask DetachAsync(PluginChannel channel);
}
