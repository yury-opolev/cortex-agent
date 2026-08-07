namespace Cortex.Contained.Bridge.Connectors.Replay;

/// <summary>
/// Provides missed outbound messages for a connector that is re-attaching after being offline.
/// </summary>
public interface IConnectorReplaySource
{
    /// <summary>
    /// Returns the outbound messages for <paramref name="channelId"/> that are strictly newer
    /// than <paramref name="since"/>, oldest first, bounded by the configured caps.
    /// Returns an empty list when history is unavailable — replay is best-effort and must never
    /// prevent a connector from attaching.
    /// </summary>
    Task<IReadOnlyList<ConnectorReplayMessage>> GetMissedMessagesAsync(
        string channelId,
        DateTimeOffset since,
        CancellationToken ct);
}
