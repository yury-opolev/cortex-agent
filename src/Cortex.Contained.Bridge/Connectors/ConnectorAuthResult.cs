namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Result returned by <see cref="IConnectorAuthenticator.AuthenticateAsync"/>.</summary>
public sealed record ConnectorAuthResult
{
    /// <summary>The authentication outcome.</summary>
    public required ConnectorAuthOutcome Outcome { get; init; }

    /// <summary>
    /// A newly issued durable token, sent to the connector in a <c>paired</c> frame.
    /// Null when the connector presented a token that was already valid.
    /// </summary>
    public string? IssuedToken { get; init; }

    /// <summary>
    /// The human-readable code shown by both the connector and the Web UI.
    /// Set when <see cref="Outcome"/> is <see cref="ConnectorAuthOutcome.PairingRequired"/>.
    /// </summary>
    public string? PairingCode { get; init; }

    /// <summary>Expiry time for the pairing code or issued token.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Denial reason, sent in a <c>pairing_denied</c> frame.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Completes when the pending pairing request is approved or denied.
    /// Null unless <see cref="Outcome"/> is <see cref="ConnectorAuthOutcome.PairingRequired"/>.
    /// </summary>
    public Task<ConnectorAuthResult>? PairingCompletion { get; init; }

    /// <summary>Creates an approved result, optionally carrying a newly issued token.</summary>
    public static ConnectorAuthResult Approved(string? issuedToken = null) =>
        new() { Outcome = ConnectorAuthOutcome.Approved, IssuedToken = issuedToken };

    /// <summary>Creates a denied result carrying the reason.</summary>
    public static ConnectorAuthResult Denied(string reason) =>
        new() { Outcome = ConnectorAuthOutcome.Denied, Reason = reason };
}
