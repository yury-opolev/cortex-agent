namespace Cortex.Contained.Channels.CloudMessaging.Auth;

/// <summary>
/// Abstracts how the home Bridge proves its identity to the AI Messenger cloud service.
/// The implementation may use an Entra client-credentials OAuth2 flow (certificate-based)
/// or a provisioned per-bridge token. Kept behind an interface so the credential mechanism
/// can be swapped without touching the channel.
/// </summary>
public interface IBridgeCredentialProvider
{
    /// <summary>
    /// Returns a bearer token that the Bridge uses when calling
    /// <c>POST /negotiate-bridge</c> on the cloud service.
    /// Implementations must cache the token and refresh it before expiry.
    /// Must not return null or empty.
    /// </summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);
}
