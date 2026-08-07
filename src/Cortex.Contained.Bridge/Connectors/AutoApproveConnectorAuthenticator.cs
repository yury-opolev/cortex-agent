using System.Security.Cryptography;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Development stub for <see cref="IConnectorAuthenticator"/> that automatically
/// approves every connector attach request.
/// </summary>
/// <remarks>
/// This class is no longer registered in the DI container. It is retained for use as a test double.
/// Use <c>ConnectorPairingService</c> for production use.
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
