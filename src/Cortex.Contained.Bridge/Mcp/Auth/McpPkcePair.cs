namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// A PKCE (RFC 7636) verifier/challenge pair for one authorization request. The
/// <see cref="Verifier"/> is kept host-side and sent only on the token exchange; the
/// <see cref="Challenge"/> (S256) is what travels in the authorization URL.
/// </summary>
public sealed record McpPkcePair
{
    /// <summary>The high-entropy code verifier (base64url, 43..128 chars).</summary>
    public required string Verifier { get; init; }

    /// <summary>The S256 code challenge: <c>BASE64URL(SHA256(ASCII(verifier)))</c>.</summary>
    public required string Challenge { get; init; }
}
