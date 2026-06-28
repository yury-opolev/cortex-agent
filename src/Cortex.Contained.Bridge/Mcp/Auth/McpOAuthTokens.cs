namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// The persisted OAuth state for one MCP server: the access/refresh tokens plus the client
/// identity and token endpoint needed to refresh them. Stored only in DPAPI (never in config or
/// logs). Carries the client secret for confidential clients; null for public (PKCE-only) clients.
/// </summary>
public sealed record McpOAuthTokens
{
    /// <summary>The current bearer access token.</summary>
    public required string AccessToken { get; init; }

    /// <summary>The refresh token, or null when the AS did not issue one.</summary>
    public string? RefreshToken { get; init; }

    /// <summary>Access-token expiry as Unix milliseconds (0 = unknown ⇒ treated as expired).</summary>
    public long ExpiresAtMs { get; init; }

    /// <summary>The registered/configured OAuth client id used to obtain these tokens.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client secret for a confidential client, or null for a public client.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>The token endpoint used for the original exchange and subsequent refreshes.</summary>
    public required string TokenEndpoint { get; init; }

    /// <summary>The scope granted (sent on refresh), or null.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// True when the access token is at or past expiry once <paramref name="skewMs"/> of safety
    /// margin is applied. A zero/unset <see cref="ExpiresAtMs"/> is treated as expired so a refresh
    /// is attempted rather than sending a possibly-stale token.
    /// </summary>
    public bool IsExpired(long nowUnixMs, long skewMs)
    {
        return this.ExpiresAtMs <= nowUnixMs + skewMs;
    }
}
