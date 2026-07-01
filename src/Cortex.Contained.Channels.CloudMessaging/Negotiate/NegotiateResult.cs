namespace Cortex.Contained.Channels.CloudMessaging.Negotiate;

/// <summary>
/// Result returned by the two-step S2S negotiate flow
/// (<c>POST /oauth2/token</c> → <c>POST /negotiate-bridge</c>).
/// <see cref="HubUrl"/> is the fully-qualified SignalR hub URL the bridge should
/// connect to (e.g. <c>https://api.example.com/hub</c>).
/// </summary>
public sealed record NegotiateResult
{
    /// <summary>
    /// Fully-qualified SignalR hub URL (scheme + host + path).
    /// Pass as the first argument to <see cref="Microsoft.AspNetCore.SignalR.Client.HubConnection"/>.
    /// </summary>
    public required string HubUrl { get; init; }

    /// <summary>
    /// Tenant IDs this bridge is authorised to serve (returned by the service).
    /// Used to validate inbound envelope <c>tenantId</c> values.
    /// </summary>
    public required IReadOnlyList<string> Tenants { get; init; }
}
