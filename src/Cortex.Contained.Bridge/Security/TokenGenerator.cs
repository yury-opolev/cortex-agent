using System.Security.Cryptography;

namespace Cortex.Contained.Bridge.Security;

/// <summary>
/// Generates cryptographically random tokens for hub authentication.
/// </summary>
public static class TokenGenerator
{
    private const int DefaultTokenLengthBytes = 32; // 256-bit

    /// <summary>
    /// Generates a 256-bit cryptographically random token encoded as Base64.
    /// </summary>
    public static string GenerateHubToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(DefaultTokenLengthBytes);
        return Convert.ToBase64String(bytes);
    }
}
