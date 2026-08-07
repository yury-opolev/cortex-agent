namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Authenticates connector attach requests.
/// Phase 1 uses <see cref="AutoApproveConnectorAuthenticator"/>;
/// Phase 2 replaces this with the real pairing service.
/// </summary>
public interface IConnectorAuthenticator
{
    /// <summary>Validates a presented token or begins a pairing flow.</summary>
    ValueTask<ConnectorAuthResult> AuthenticateAsync(ConnectorAuthRequest request, CancellationToken ct);
}
