using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Binds an <see cref="IMcpOAuthManager"/> to one server so the HTTP transport can fetch a valid
/// bearer (refresh-on-expiry) and force a refresh after a mid-session 401, all on the host.
/// </summary>
public sealed class McpOAuthBearerSource : IMcpBearerSource
{
    private readonly IMcpOAuthManager oauthManager;
    private readonly McpServerConfig server;

    public McpOAuthBearerSource(IMcpOAuthManager oauthManager, McpServerConfig server)
    {
        this.oauthManager = oauthManager;
        this.server = server;
    }

    public Task<string?> GetAsync(CancellationToken cancellationToken)
        => this.oauthManager.GetAccessTokenAsync(this.server, cancellationToken);

    public Task<string?> RefreshAsync(CancellationToken cancellationToken)
        => this.oauthManager.RefreshAccessTokenAsync(this.server.Key, cancellationToken);
}
