using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the OAuth 2.1 loopback callback <c>GET /mcp/oauth/callback</c> on the existing Kestrel
/// <c>:5080</c> (no new port). The authorization server redirects the user's browser here with a
/// <c>code</c> + <c>state</c>; the endpoint correlates by the single-use <see cref="McpOAuthStart.State"/>
/// and completes the code exchange. It is intentionally unauthenticated (the AS redirect carries no
/// Bridge session) — replay/forgery protection is the cryptographic, single-use, expiring state,
/// validated entirely inside <see cref="IMcpOAuthManager.CompleteAsync"/>.
/// </summary>
internal static class McpOAuthCallbackEndpoint
{
    /// <summary>Maps <c>GET /mcp/oauth/callback</c> onto <paramref name="app"/>.</summary>
    public static void MapMcpOAuthCallbackEndpoint(this WebApplication app)
    {
        app.MapGet("/mcp/oauth/callback", async (
            string? code,
            string? state,
            string? error,
            IMcpOAuthManager oauthManager,
            McpHostService hostService,
            McpConfigStore configStore,
            CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrEmpty(error))
            {
                return Page("Authorization was denied or failed. You can close this window.");
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                return Page("Missing authorization response. You can close this window.");
            }

            var completion = await oauthManager.CompleteAsync(state, code, cancellationToken).ConfigureAwait(false);
            if (!completion.Success)
            {
                return Page("Could not complete authorization. You can close this window.");
            }

            // The server now has tokens — reconcile so it connects live. Fire-and-forget so the
            // browser response is never blocked by spawning/handshaking the MCP server.
            _ = Task.Run(() => hostService.ReconcileAsync(configStore.GetSettings(), CancellationToken.None), CancellationToken.None);

            return Page("Connected. You can close this window and return to Cortex.");
        });
    }

    private static IResult Page(string message)
    {
        var html = $"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>Cortex MCP</title></head>
        <body style="font-family: system-ui, sans-serif; padding: 2rem;">
        <p>{System.Net.WebUtility.HtmlEncode(message)}</p>
        </body></html>
        """;
        return Results.Content(html, "text/html");
    }
}
