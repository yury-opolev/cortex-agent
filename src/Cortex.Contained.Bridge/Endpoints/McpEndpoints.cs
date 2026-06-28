using System.Diagnostics;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Auth;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the MCP server-management endpoints (<c>/api/mcp/*</c>): list/add/edit/delete servers, master
/// toggle, OAuth connect (opens the host browser), and a test-connect handshake. Every response is
/// <b>secret-redacted</b> — only <c>hasSecret</c>/<c>secretRef</c> are surfaced, never a secret value —
/// and every endpoint requires authorization. Changes persist through <see cref="McpConfigStore"/> and
/// trigger a live reconcile, which re-pushes the aggregated catalog to connected agents.
/// </summary>
internal static class McpEndpoints
{
    /// <summary>Maps the <c>/api/mcp/*</c> endpoints onto <paramref name="app"/>.</summary>
    public static void MapMcpEndpoints(this WebApplication app)
    {
        // List all configured servers with live status + tool counts (secrets redacted).
        app.MapGet("/api/mcp/servers", (
            McpConfigStore configStore,
            McpHostService hostService,
            IMcpOAuthManager oauthManager) =>
        {
            var settings = configStore.GetSettings();
            var servers = settings.Servers
                .Select(server => McpServerProjection.Project(
                    server,
                    settings.Enabled,
                    hostService.GetServerInfo(server.Key),
                    NeedsLogin(server, oauthManager)))
                .ToList();

            return Results.Ok(new { enabled = settings.Enabled, servers });
        }).RequireAuthorization();

        // Add a new server.
        app.MapPost("/api/mcp/servers", (
            McpServerRequest request,
            McpConfigStore configStore,
            McpHostService hostService,
            SecretManager secretManager) =>
        {
            var settings = configStore.GetSettings();

            var keyError = McpServerRequestMapper.ValidateNewKey(request.Key, settings.Servers.Select(s => s.Key));
            if (keyError is not null)
            {
                return Results.Json(new { error = keyError }, statusCode: 400);
            }

            var config = McpServerRequestMapper.ToConfig(request);
            ApplySecret(secretManager, config, request.Secret);

            settings.Servers.Add(config);
            configStore.Save(settings);
            FireReconcile(hostService, configStore);

            return Results.Ok(new { success = true, key = config.Key });
        }).RequireAuthorization();

        // Edit an existing server (incl. enable, allow-list, secret → DPAPI).
        app.MapPut("/api/mcp/servers/{key}", (
            string key,
            McpServerRequest request,
            McpConfigStore configStore,
            McpHostService hostService,
            SecretManager secretManager) =>
        {
            var settings = configStore.GetSettings();
            var config = FindServer(settings, key);
            if (config is null)
            {
                return Results.Json(new { error = $"No MCP server with key '{key}'." }, statusCode: 404);
            }

            McpServerRequestMapper.ApplyTo(config, request);
            ApplySecret(secretManager, config, request.Secret);

            configStore.Save(settings);
            FireReconcile(hostService, configStore);

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Delete a server (and its static API-key secret).
        app.MapDelete("/api/mcp/servers/{key}", (
            string key,
            McpConfigStore configStore,
            McpHostService hostService,
            SecretManager secretManager) =>
        {
            var settings = configStore.GetSettings();
            var config = FindServer(settings, key);
            if (config is null)
            {
                return Results.Json(new { error = $"No MCP server with key '{key}'." }, statusCode: 404);
            }

            settings.Servers.Remove(config);
            if (!string.IsNullOrEmpty(config.SecretRef))
            {
                secretManager.RemoveApiKey(config.SecretRef);
            }

            configStore.Save(settings);
            FireReconcile(hostService, configStore);

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Test-connect: spawn/handshake/list and return the discovered tools or the error/stderr.
        app.MapPost("/api/mcp/servers/{key}/test", async (
            string key,
            McpConfigStore configStore,
            IMcpServerConnectionFactory factory,
            CancellationToken cancellationToken) =>
        {
            var config = FindServer(configStore.GetSettings(), key);
            if (config is null)
            {
                return Results.Json(new { error = $"No MCP server with key '{key}'." }, statusCode: 404);
            }

            var connection = factory.TryCreate(config);
            if (connection is null)
            {
                return Results.Ok(new
                {
                    ok = false,
                    error = "Could not create a connection — check the transport fields and auth. OAuth servers must be connected first.",
                });
            }

            try
            {
                await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (connection.Status == McpServerStatus.Connected)
                {
                    return Results.Ok(new
                    {
                        ok = true,
                        tools = connection.Tools.Select(t => new { name = t.ToolName, description = t.Description }).ToList(),
                    });
                }

                return Results.Ok(new { ok = false, error = connection.LastError ?? connection.Status.ToString() });
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }).RequireAuthorization();

        // Begin the OAuth consent flow: build the authorization URL + open the host system browser.
        // The loopback callback (GET /mcp/oauth/callback) completes the exchange and reconnects.
        app.MapPost("/api/mcp/servers/{key}/connect", async (
            string key,
            McpConfigStore configStore,
            IMcpOAuthManager oauthManager,
            CancellationToken cancellationToken) =>
        {
            var config = FindServer(configStore.GetSettings(), key);
            if (config is null)
            {
                return Results.Json(new { error = $"No MCP server with key '{key}'." }, statusCode: 404);
            }

            if (config.Transport != McpTransport.Http)
            {
                return Results.Json(new { error = "OAuth connect applies to HTTP servers only." }, statusCode: 400);
            }

            try
            {
                var start = await oauthManager.BuildAuthorizationUrlAsync(config, cancellationToken).ConfigureAwait(false);
                var browserOpened = TryOpenBrowser(start.AuthorizationUrl, out var browserError);
                return Results.Ok(new
                {
                    success = true,
                    status = "needsLogin",
                    authUrl = start.AuthorizationUrl,
                    browserOpened,
                    browserError,
                });
            }
#pragma warning disable CA1031 // Surface any discovery/registration failure as a readable error, not a 500 page.
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Could not start OAuth: {ex.Message}" }, statusCode: 500);
            }
#pragma warning restore CA1031
        }).RequireAuthorization();

        // Force a disconnect + reconnect of one server (picks up a rotated secret without a restart).
        app.MapPost("/api/mcp/servers/{key}/reconnect", (
            string key,
            McpConfigStore configStore,
            McpHostService hostService) =>
        {
            var config = FindServer(configStore.GetSettings(), key);
            if (config is null)
            {
                return Results.Json(new { error = $"No MCP server with key '{key}'." }, statusCode: 404);
            }

            // Fire-and-forget like the other mutating endpoints: spawning/handshaking must not block
            // the HTTP save. The reconcile re-pushes the catalog when it changes.
            _ = Task.Run(() => hostService.ForceReconnectAsync(config.Key, configStore.GetSettings(), CancellationToken.None));

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Master MCP enable toggle (live: drops/restores all tools).
        app.MapPost("/api/mcp/toggle", (
            McpToggleRequest request,
            McpConfigStore configStore,
            McpHostService hostService) =>
        {
            if (request?.Enabled is null)
            {
                return Results.Json(new { error = "enabled is required" }, statusCode: 400);
            }

            var settings = configStore.GetSettings();
            settings.Enabled = request.Enabled.Value;
            configStore.Save(settings);
            FireReconcile(hostService, configStore);

            return Results.Ok(new { success = true, enabled = settings.Enabled });
        }).RequireAuthorization();
    }

    private static bool NeedsLogin(McpServerConfig server, IMcpOAuthManager oauthManager)
    {
        return server.Transport == McpTransport.Http
            && server.Auth == McpAuthMode.OAuth
            && !oauthManager.HasTokens(server);
    }

    private static McpServerConfig? FindServer(McpSettingsConfig settings, string key)
    {
        return settings.Servers.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Applies the write-only secret field to DPAPI: <c>null</c> leaves it unchanged, empty clears it,
    /// otherwise it is stored under a deterministic ref id and <see cref="McpServerConfig.SecretRef"/> set.
    /// </summary>
    private static void ApplySecret(SecretManager secretManager, McpServerConfig config, string? secret)
    {
        if (secret is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!string.IsNullOrEmpty(config.SecretRef))
            {
                secretManager.RemoveApiKey(config.SecretRef);
            }

            config.SecretRef = null;
            return;
        }

        var secretId = McpServerRequestMapper.ApiKeySecretId(config.Key);
        secretManager.StoreApiKey(secretId, secret);
        config.SecretRef = secretId;
    }

    private static void FireReconcile(McpHostService hostService, McpConfigStore configStore)
    {
        // Fire-and-forget: spawning/handshaking servers must not block the HTTP save. The reconcile
        // raises CatalogChanged, which the catalog pusher (subscribed at startup) uses to live re-push.
        _ = Task.Run(() => hostService.ReconcileAsync(configStore.GetSettings(), CancellationToken.None));
    }

    private static bool TryOpenBrowser(string url, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            error = null;
            return true;
        }
#pragma warning disable CA1031 // Browser open is best-effort; the URL is returned for manual opening.
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
#pragma warning restore CA1031
    }
}
