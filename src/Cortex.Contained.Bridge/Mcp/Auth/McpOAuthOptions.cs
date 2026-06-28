namespace Cortex.Contained.Bridge.Mcp.Auth;

/// <summary>
/// Host-side OAuth options for the MCP plugin system. The <see cref="RedirectUri"/> is the loopback
/// callback served on the existing Kestrel <c>:5080</c> — no new port/firewall surface.
/// </summary>
public sealed class McpOAuthOptions
{
    /// <summary>The loopback redirect URI the authorization server calls back with the code.</summary>
    public string RedirectUri { get; set; } = "http://127.0.0.1:5080/mcp/oauth/callback";

    /// <summary>The client name advertised during Dynamic Client Registration.</summary>
    public string ClientName { get; set; } = "Cortex";

    /// <summary>How long a pending authorization (state) remains valid before it is rejected.</summary>
    public TimeSpan PendingTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Safety margin before token expiry at which a proactive refresh is triggered.</summary>
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromMinutes(1);
}
