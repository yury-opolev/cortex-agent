namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// The OAuth 2.1 authorization-server endpoints discovered from an
/// <c>oauth-authorization-server</c> / OIDC <c>openid-configuration</c> metadata document.
/// </summary>
public sealed record McpAuthServerEndpoints
{
    /// <summary>The authorization endpoint (where the user consents).</summary>
    public required string AuthorizationEndpoint { get; init; }

    /// <summary>The token endpoint (code → access/refresh token exchange + refresh).</summary>
    public required string TokenEndpoint { get; init; }

    /// <summary>The Dynamic Client Registration endpoint (RFC 7591), or null when unsupported.</summary>
    public string? RegistrationEndpoint { get; init; }

    /// <summary>Scopes advertised as supported by the authorization server (may be empty).</summary>
    public IReadOnlyList<string> ScopesSupported { get; init; } = [];
}
