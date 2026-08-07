namespace Cortex.Contained.Bridge.Connectors.Security;

/// <summary>
/// Token-free view of a paired connector, safe to hand to the REST layer and the Web UI.
/// </summary>
/// <remarks>
/// <see cref="ConnectorRecord"/> deliberately carries the durable token because it is the
/// shape persisted in the DPAPI-encrypted registry blob. Nothing outside
/// <see cref="ConnectorTokenStore"/> and the pairing service should ever see that type, so
/// every read path exposed to callers projects to this summary instead.
/// </remarks>
public sealed record ConnectorSummary
{
    /// <summary>The plugin channel id, <c>plugin:&lt;key&gt;:&lt;instance&gt;</c>.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Connector type key.</summary>
    public required string Key { get; init; }

    /// <summary>Connector instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Connector-supplied display name. UNTRUSTED — escape wherever rendered.</summary>
    public required string DisplayName { get; init; }

    /// <summary>When the connector was paired.</summary>
    public required DateTimeOffset PairedAt { get; init; }

    /// <summary>When the connector was last seen, or null if it has not attached since pairing.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Whether the connector is allowed to attach.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Projects a stored record, dropping the token.</summary>
    /// <param name="record">The stored connector record.</param>
    public static ConnectorSummary FromRecord(ConnectorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ConnectorSummary
        {
            ChannelId = record.ChannelId,
            Key = record.Key,
            InstanceId = record.InstanceId,
            DisplayName = record.DisplayName,
            PairedAt = record.PairedAt,
            LastSeenAt = record.LastSeenAt,
            Enabled = record.Enabled,
        };
    }
}
