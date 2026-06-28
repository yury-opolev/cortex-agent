using System.Security.Cryptography;
using System.Text;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// PKCE (RFC 7636) helpers for the OAuth 2.1 authorization-code flow. Uses the <c>S256</c>
/// challenge method exclusively (plain is disallowed by OAuth 2.1). Pure + deterministic given a
/// verifier; <see cref="Generate"/> draws a cryptographically random verifier.
/// </summary>
public static class McpPkce
{
    private const int VerifierByteLength = 32; // 32 bytes → 43-char base64url verifier.

    /// <summary>Generates a cryptographically random verifier and its matching S256 challenge.</summary>
    public static McpPkcePair Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(VerifierByteLength);
        var verifier = Base64UrlEncode(verifierBytes);
        return new McpPkcePair
        {
            Verifier = verifier,
            Challenge = ComputeChallenge(verifier),
        };
    }

    /// <summary>Computes the S256 code challenge: <c>BASE64URL(SHA256(ASCII(verifier)))</c>.</summary>
    public static string ComputeChallenge(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
