namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>URL security checks for MCP HTTP endpoints that carry credentials.</summary>
internal static class McpUrlSecurity
{
    /// <summary>
    /// True when attaching credentials to <paramref name="url"/> would transmit them in cleartext:
    /// an <c>http://</c> endpoint whose host is NOT loopback. Loopback http (a local MCP server on
    /// 127.0.0.1/localhost) is allowed; https is always allowed. An unparseable URL returns false
    /// (the connection attempt fails elsewhere with a clearer error).
    /// </summary>
    public static bool IsInsecureForCredentials(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !uri.IsLoopback;
    }

    /// <summary>
    /// True when <paramref name="url"/> is an acceptable OAuth discovery/authorization/token
    /// endpoint to fetch or POST secrets to: <c>https</c> anywhere, or <c>http</c> only on loopback
    /// (a local authorization server). Everything else is rejected — this blocks both SSRF to
    /// internal/cleartext hosts (e.g. <c>http://169.254.169.254/…</c>) reached via server-controlled
    /// discovery metadata, and exfiltration of the auth code / PKCE verifier / client secret to a
    /// plaintext or attacker-nominated token endpoint. An unparseable URL is rejected.
    /// </summary>
    public static bool IsAllowedOAuthEndpoint(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && uri.IsLoopback;
    }
}
