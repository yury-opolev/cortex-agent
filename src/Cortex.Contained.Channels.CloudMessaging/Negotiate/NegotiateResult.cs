namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// Result returned by <c>POST /negotiate-bridge</c>.
/// The service issues a Web PubSub client URL scoped to the tenant groups
/// this bridge serves, and returns the resolved tenant list.
/// </summary>
public sealed record NegotiateResult
{
    /// <summary>
    /// Web PubSub client WebSocket URL with embedded access token.
    /// Connect directly — no separate auth header needed.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Tenant IDs this bridge is authorised to serve (returned by the service).
    /// Used to validate inbound envelope <c>tenantId</c> values.
    /// </summary>
    public required IReadOnlyList<string> Tenants { get; init; }
}
