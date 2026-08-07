namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Authenticates connector attach requests: validates a presented durable token, or begins the
/// human pairing flow when there is no valid one. Implemented by
/// <see cref="Pairing.ConnectorPairingService"/>.
/// </summary>
public interface IConnectorAuthenticator
{
    /// <summary>Validates a presented token or begins a pairing flow.</summary>
    ValueTask<ConnectorAuthResult> AuthenticateAsync(ConnectorAuthRequest request, CancellationToken ct);
}
