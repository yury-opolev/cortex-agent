namespace Cortex.Contained.Bridge.Connectors.Pairing;

/// <summary>Represents a pending pairing request awaiting a human decision.</summary>
public sealed record ConnectorPairingRequest
{
    /// <summary>Unique request identifier.</summary>
    public required string RequestId { get; init; }

    /// <summary>The plugin channel id this request is for.</summary>
    public required string ChannelId { get; init; }

    /// <summary>Connector type key.</summary>
    public required string Key { get; init; }

    /// <summary>Connector instance identifier.</summary>
    public required string InstanceId { get; init; }

    /// <summary>Connector-supplied display name. UNTRUSTED — escape wherever rendered.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The human-transcribable pairing code shown in both the connector and the Web UI.</summary>
    public required string Code { get; init; }

    /// <summary>Remote endpoint of the requesting connector.</summary>
    public required string RemoteEndpoint { get; init; }

    /// <summary>Timestamp when this request was created.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Timestamp after which the request is no longer valid.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
