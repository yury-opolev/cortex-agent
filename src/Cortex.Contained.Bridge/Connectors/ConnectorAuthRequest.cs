namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Authentication request submitted to <see cref="IConnectorAuthenticator"/>.</summary>
public sealed record ConnectorAuthRequest
{
    /// <summary>Normalised connector type key (e.g. <c>terminal</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Normalised connector instance identifier (e.g. <c>default</c>).</summary>
    public required string InstanceId { get; init; }

    /// <summary>Human-readable name advertised by the connector.</summary>
    public required string DisplayName { get; init; }

    /// <summary>DPAPI-stored pairing token presented by the connector; null on first connect.</summary>
    public string? Token { get; init; }

    /// <summary>Remote endpoint address of the connector, used for diagnostics and policy.</summary>
    public required string RemoteEndpoint { get; init; }
}
