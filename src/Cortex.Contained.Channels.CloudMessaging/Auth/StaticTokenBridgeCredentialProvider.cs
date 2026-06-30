namespace Cortex.Contained.Channels.CloudMessaging.Auth;

/// <summary>
/// Simple <see cref="IBridgeCredentialProvider"/> backed by a pre-provisioned static token
/// (loaded from DPAPI secret store via <c>cortex.yml</c>). Suitable for the MVP where a
/// per-bridge token is issued at setup time. Replace with a cert-based Entra OAuth2 flow
/// when certificate credentials are provisioned.
/// </summary>
public sealed class StaticTokenBridgeCredentialProvider : IBridgeCredentialProvider
{
    private readonly string token;

    /// <param name="token">Pre-provisioned bridge token (resolved from DPAPI, never plaintext config).</param>
    public StaticTokenBridgeCredentialProvider(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Bridge token must not be null or empty.", nameof(token));
        }

        this.token = token;
    }

    /// <inheritdoc />
    public Task<string> GetTokenAsync(CancellationToken ct = default)
        => Task.FromResult(this.token);
}
