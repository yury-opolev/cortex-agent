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
}
