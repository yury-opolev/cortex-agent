using Cortex.Contained.Bridge.Connectors.Security;

namespace Cortex.Contained.Bridge.Connectors.Pairing;

/// <summary>
/// Seam between the Web UI and the connector pairing service.
/// The REST layer never talks to <see cref="IConnectorAuthenticator"/> directly.
/// </summary>
public interface IConnectorPairingCoordinator
{
    /// <summary>Pairing requests awaiting a human decision, oldest first. Expired requests are excluded.</summary>
    IReadOnlyList<ConnectorPairingRequest> GetPendingRequests();

    /// <summary>Approves a pending request. Returns false when the id is unknown or already resolved.</summary>
    bool Approve(string requestId);

    /// <summary>Denies a pending request. Returns false when the id is unknown or already resolved.</summary>
    bool Deny(string requestId, string reason);

    /// <summary>
    /// All paired connectors, including those not currently attached. The durable token is
    /// deliberately not part of <see cref="ConnectorSummary"/> so it cannot leak through a
    /// REST response.
    /// </summary>
    IReadOnlyList<ConnectorSummary> GetPairedConnectors();

    /// <summary>Revokes a pairing so the connector must pair again. Returns false when unknown.</summary>
    Task<bool> RevokeAsync(string channelId);

    /// <summary>Enables or disables a paired connector without losing its pairing. Returns false when unknown.</summary>
    Task<bool> SetEnabledAsync(string channelId, bool enabled);
}
