using System.Security.Cryptography;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Development stub for <see cref="IConnectorAuthenticator"/> that automatically
/// approves every connector attach request.
/// </summary>
/// <remarks>
/// This class is replaced by the real pairing service in Phase 2.
/// It accepts ANY presented token without validation and issues a fresh
/// Base64URL-encoded random token when no token is presented.
/// </remarks>
public sealed class AutoApproveConnectorAuthenticator : IConnectorAuthenticator
{
    /// <inheritdoc/>
    public ValueTask<ConnectorAuthResult> AuthenticateAsync(ConnectorAuthRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');
            return ValueTask.FromResult(ConnectorAuthResult.Approved(token));
        }

        return ValueTask.FromResult(ConnectorAuthResult.Approved(null));
    }
}
