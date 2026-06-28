namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Supplies the current OAuth bearer token for one MCP server and forces a refresh after a 401.
/// Lets <see cref="McpOAuthRefreshHandler"/> attach a freshly-valid token on each request and do
/// transparent refresh-and-retry without coupling the HTTP transport to the OAuth manager.
/// </summary>
public interface IMcpBearerSource
{
    /// <summary>Returns the current valid access token (refreshing on expiry), or null when none.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Forces a refresh after a 401 and returns the new access token, or null when impossible.</summary>
    Task<string?> RefreshAsync(CancellationToken cancellationToken);
}
