namespace Cortex.Contained.Bridge.Connectors.Security;

/// <summary>Represents a paired connector stored in the DPAPI-backed registry.</summary>
/// <remarks>
/// This type carries the durable token because it is the shape persisted in the
/// DPAPI-encrypted registry blob. It must never be handed to the REST layer — use
/// <see cref="ConnectorSummary"/> for anything a caller outside the pairing service sees.
/// </remarks>
public sealed record ConnectorRecord
{
    /// <summary>The plugin channel id (e.g. <c>plugin:terminal:default</c>).</summary>
    public required string ChannelId { get; init; }

    /// <summary>Connector type key (e.g. <c>terminal</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Connector instance identifier (e.g. <c>default</c>).</summary>
    public required string InstanceId { get; init; }

    /// <summary>Human-readable display name advertised by the connector.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The durable token issued to this connector, held DPAPI-encrypted at rest.</summary>
    public required string Token { get; init; }

    /// <summary>Timestamp when this connector was paired.</summary>
    public required DateTimeOffset PairedAt { get; init; }

    /// <summary>Timestamp of the last successful attach, or null if never seen since pairing.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Per-connector toggle. A disabled connector is refused at attach without losing its pairing.</summary>
    public bool Enabled { get; init; } = true;
}
