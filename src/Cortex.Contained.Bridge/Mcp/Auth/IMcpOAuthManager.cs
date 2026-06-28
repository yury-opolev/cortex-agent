using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Orchestrates OAuth 2.1 (auto-discovery + DCR + PKCE) for HTTP MCP servers on the host: begins the
/// browser consent flow, completes the loopback code exchange, and supplies valid bearer tokens
/// (refreshing on expiry). All tokens live in DPAPI; the agent never sees them.
/// </summary>
public interface IMcpOAuthManager
{
    /// <summary>True when valid or refreshable tokens are already stored for <paramref name="server"/>.</summary>
    bool HasTokens(McpServerConfig server);

    /// <summary>
    /// Discovers the authorization server (401 → metadata), registers a client if needed (DCR), and
    /// builds the authorization URL (PKCE + state). The returned state is single-use.
    /// </summary>
    Task<McpOAuthStart> BuildAuthorizationUrlAsync(McpServerConfig server, CancellationToken cancellationToken);

    /// <summary>
    /// Completes a flow from the loopback callback: validates the (single-use, unexpired) state,
    /// exchanges the code for tokens, and stores them. Rejects unknown/replayed/expired state.
    /// </summary>
    Task<McpOAuthCompletion> CompleteAsync(string state, string code, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a valid access token for <paramref name="server"/>, refreshing it when expired, or
    /// null when no usable tokens exist (the server then needs an interactive login).
    /// </summary>
    Task<string?> GetAccessTokenAsync(McpServerConfig server, CancellationToken cancellationToken);

    /// <summary>
    /// Forces a refresh for <paramref name="serverKey"/> (e.g. after a mid-session 401) and returns
    /// the new access token, or null when refresh is impossible.
    /// </summary>
    Task<string?> RefreshAccessTokenAsync(string serverKey, CancellationToken cancellationToken);

    /// <summary>
    /// Removes any stored OAuth tokens for <paramref name="serverKey"/> (e.g. when the server is
    /// deleted) so no orphaned credentials remain in DPAPI.
    /// </summary>
    void ClearTokens(string serverKey);
}
