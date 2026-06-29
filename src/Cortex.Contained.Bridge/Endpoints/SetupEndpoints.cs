using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Common.Auth;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the first-run setup wizard endpoints (<c>/api/setup/*</c>): provider templates,
/// model fetching, the Copilot/Anthropic OAuth flows, and the final save.
/// All endpoints require Bridge session authorization.
/// </summary>
internal static class SetupEndpoints
{
    /// <summary>
    /// Maps the <c>/api/setup/*</c> endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The web application to register endpoints on.</param>
    /// <param name="cortexConfigPath">Absolute path to <c>cortex.yml</c> for save persistence.</param>
    public static void MapSetupEndpoints(this WebApplication app, string cortexConfigPath)
    {
        // --- Setup API ---
        app.MapGet("/api/setup/status", (BridgeConfig config) =>
        {
            var hasConfigured = config.LlmProviders.Exists(p => !string.IsNullOrEmpty(p.ApiKey));
            return Results.Ok(new { needsSetup = !hasConfigured });
        }).RequireAuthorization();

        app.MapGet("/api/setup/providers", () =>
            Results.Ok(SetupHelpers.GetProviderTemplates())).RequireAuthorization();

        app.MapPost("/api/setup/fetch-models", async (FetchModelsRequest request, IHttpClientFactory httpFactory) =>
        {
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var models = await SetupHelpers.FetchAvailableModelsAsync(
                    request.Provider, request.ApiKey, httpClient, request.TokenType, request.GithubBaseUrl).ConfigureAwait(false);
                return Results.Ok(models);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = $"Failed to fetch models: {ex.Message}" }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to fetch models: {ex.Message}" }, statusCode: 500);
            }
        }).RequireAuthorization();

        // Returns GitHub Copilot model IDs from models.dev — no API key or OAuth token required.
        // Useful for populating the model selection list before the OAuth device flow completes.
        app.MapGet("/api/setup/copilot-models", async (IHttpClientFactory httpFactory) =>
        {
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var models = await SetupHelpers.FetchCopilotModelsFromModelsDevAsync(httpClient).ConfigureAwait(false);
                return Results.Ok(models);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = $"Failed to fetch models from models.dev: {ex.Message}" }, statusCode: 502);
            }
        }).RequireAuthorization();

        // --- Copilot OAuth Device Flow ---
        app.MapPost("/api/setup/copilot-auth", async (CopilotAuthRequest request, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("CopilotSetup");
            var host = GitHubOAuthUrls.NormalizeBaseUrl(request.GithubBaseUrl);
            var customClientId = !string.IsNullOrWhiteSpace(request.ClientId);
            CopilotSetupLog.DeviceFlowInit(log, host, customClientId);
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var result = await SetupHelpers.InitiateCopilotDeviceFlowAsync(httpClient, request.ClientId, request.GithubBaseUrl).ConfigureAwait(false);
                CopilotSetupLog.DeviceFlowInitOk(log, host);
                return Results.Ok(result);
            }
            catch (CopilotSetupException ex)
            {
                CopilotSetupLog.DeviceFlowInitFailed(log, host, ex.StatusCode, ex.Message);
                return Results.Json(new { error = ex.Message, status = ex.StatusCode }, statusCode: 502);
            }
            catch (HttpRequestException ex)
            {
                CopilotSetupLog.DeviceFlowInitNetworkFailed(log, host, ex.Message);
                return Results.Json(new { error = $"Failed to initiate device flow: {ex.Message}" }, statusCode: 502);
            }
        }).RequireAuthorization();

        app.MapPost("/api/setup/copilot-poll", async (CopilotPollRequest request, IHttpClientFactory httpFactory, ILoggerFactory loggerFactory) =>
        {
            var log = loggerFactory.CreateLogger("CopilotSetup");
            var host = GitHubOAuthUrls.NormalizeBaseUrl(request.GithubBaseUrl);
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var result = await SetupHelpers.PollCopilotTokenAsync(request.DeviceCode, httpClient, request.ClientId, request.GithubBaseUrl).ConfigureAwait(false);
                if (string.Equals(result.Status, "failed", StringComparison.Ordinal))
                {
                    CopilotSetupLog.TokenPollFailed(log, host, result.Error);
                }
                else if (string.Equals(result.Status, "success", StringComparison.Ordinal))
                {
                    CopilotSetupLog.TokenPollSuccess(log, host);
                }

                return Results.Ok(result);
            }
            catch (HttpRequestException ex)
            {
                CopilotSetupLog.TokenPollNetworkFailed(log, host, ex.Message);
                return Results.Json(new { error = $"Failed to poll token: {ex.Message}" }, statusCode: 502);
            }
        }).RequireAuthorization();

        // --- Anthropic OAuth (Authorization Code + PKCE) ---

        // Step 1: generate PKCE pair, build auth URL, return to wizard
        app.MapPost("/api/setup/anthropic-auth", () =>
        {
            var (verifier, challenge) = SetupHelpers.GenerateAnthropicPkce();
            var authUrl = SetupHelpers.BuildAnthropicAuthUrl(challenge, verifier);
            return Results.Ok(new AnthropicAuthInitiateResponse
            {
                AuthUrl = authUrl,
                CodeVerifier = verifier,
            });
        }).RequireAuthorization();

        // Step 2: user pastes the authorization code; exchange it for tokens
        app.MapPost("/api/setup/anthropic-exchange", async (
            AnthropicExchangeRequest request,
            SecretManager secrets,
            IHttpClientFactory httpFactory) =>
        {
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var tokenResp = await SetupHelpers.ExchangeAnthropicCodeAsync(
                    request.Code, request.CodeVerifier, httpClient).ConfigureAwait(false);

                if (string.IsNullOrEmpty(tokenResp.AccessToken))
                {
                    return Results.Json(new { error = "No access token in Anthropic response." }, statusCode: 502);
                }

                var expiresAtMs = tokenResp.ExpiresIn > 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (tokenResp.ExpiresIn * 1000L)
                    : 0L;

                // Persist tokens so they survive a restart before /save is called
                secrets.StoreOAuthTokens(
                    "anthropic",
                    tokenResp.AccessToken,
                    tokenResp.RefreshToken ?? string.Empty,
                    expiresAtMs);

                return Results.Ok(new
                {
                    accessToken = tokenResp.AccessToken,
                    refreshToken = tokenResp.RefreshToken,
                    expiresAt = expiresAtMs,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { error = $"Failed to exchange code: {ex.Message}" }, statusCode: 502);
            }
        }).RequireAuthorization();

        // --- Anthropic Device Code Flow (recommended for pro subscription) ---
        // Step 1: Initiate device code flow — returns a URL the user visits to approve
        app.MapPost("/api/setup/anthropic-device-code", async (IHttpClientFactory httpFactory) =>
        {
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var result = await OAuthTokenService.InitiateDeviceCodeAsync(httpClient).ConfigureAwait(false);
                return Results.Ok(new
                {
                    deviceCode = result.DeviceCode,
                    userCode = result.UserCode,
                    verificationUrl = result.VerificationUriComplete,
                    expiresIn = result.ExpiresIn,
                    interval = result.Interval,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        }).RequireAuthorization();

        // Step 2: Poll for device code approval — call repeatedly until approved
        app.MapPost("/api/setup/anthropic-device-poll", async (
            AnthropicDevicePollRequest request,
            SecretManager secrets,
            IHttpClientFactory httpFactory) =>
        {
            try
            {
                using var httpClient = httpFactory.CreateClient();
                var tokenResp = await OAuthTokenService.PollDeviceTokenAsync(
                    request.DeviceCode, httpClient).ConfigureAwait(false);

                if (tokenResp is null)
                {
                    return Results.Ok(new { status = "pending" });
                }

                if (string.IsNullOrEmpty(tokenResp.AccessToken))
                {
                    return Results.Json(new { error = "No access token in response." }, statusCode: 502);
                }

                var expiresAtMs = tokenResp.ExpiresIn > 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (tokenResp.ExpiresIn * 1000L)
                    : 0L;

                // Persist tokens so they survive a restart
                secrets.StoreOAuthTokens(
                    "anthropic",
                    tokenResp.AccessToken,
                    tokenResp.RefreshToken ?? string.Empty,
                    expiresAtMs);

                return Results.Ok(new
                {
                    status = "approved",
                    accessToken = tokenResp.AccessToken,
                    refreshToken = tokenResp.RefreshToken,
                    expiresAt = expiresAtMs,
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { status = "error", error = ex.Message });
            }
        }).RequireAuthorization();

        // --- Anthropic Setup Token (paste from `claude setup-token`) ---
        app.MapPost("/api/setup/anthropic-setup-token", (
            AnthropicSetupTokenRequest request,
            SecretManager secrets) =>
        {
            var token = request.Token?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(token) || token.Length < 20)
            {
                return Results.BadRequest(new { error = "Token is too short. Paste the full token from 'claude setup-token'." });
            }

            try
            {
                // Store as OAuth token. Setup tokens are long-lived (1 year) with no refresh.
                // Pass null instead of empty string to avoid DPAPI encryption of empty value.
                secrets.StoreOAuthTokens("anthropic", token, null!, 0L);
                return Results.Ok(new { status = "saved" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = $"Failed to save token: {ex.Message}" }, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapPost("/api/setup/save", async (
            MultiSetupRequest request,
            SecretManager secrets,
            BridgeConfig config,
            Worker worker,
            TenantRouter tenantRouter,
            ModelCatalog modelCatalog,
            IHostEnvironment env) =>
        {
            var providerConfigs = new List<LlmProviderConfig>();

            foreach (var p in request.Providers)
            {
                var providerName = SetupHelpers.ResolveProviderName(p.Provider);

                if (p.IsExisting)
                {
                    // Keep the current runtime config entry as-is (credentials already in DPAPI)
                    var existing = config.LlmProviders.Find(ep =>
                        string.Equals(ep.Name, providerName, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        providerConfigs.Add(existing);
                    }
                }
                else
                {
                    // New provider — store credentials and build config
                    var authMethod = !string.IsNullOrEmpty(p.RefreshToken) ? "oauth" : null;
                    var tokenType = SetupHelpers.ResolveTokenType(p.Provider, authMethod);

                    if (!string.IsNullOrEmpty(p.RefreshToken))
                    {
                        secrets.StoreOAuthTokens(providerName, p.ApiKey, p.RefreshToken, p.TokenExpiresAt);
                    }
                    else
                    {
                        secrets.StoreApiKey(providerName, p.ApiKey);
                    }

                    providerConfigs.Add(new LlmProviderConfig
                    {
                        Name = providerName,
                        Api = SetupHelpers.ResolveApi(p.Provider),
                        BaseUrl = SetupHelpers.ResolveBaseUrl(p.Provider),
                        ApiKey = p.ApiKey,
                        TokenType = tokenType,
                        ClientId = p.ClientId,
                        RefreshToken = p.RefreshToken,
                        TokenExpiresAt = p.TokenExpiresAt,
                        Models = p.Models,
                        DefaultModel = p.Models.Count > 0 ? p.Models[0] : null,
                        MemoryModel = string.IsNullOrWhiteSpace(p.MemoryModel) ? null : p.MemoryModel,
                    });
                }
            }

            // Enrich model definitions with limits from models.dev catalog
            foreach (var pc in providerConfigs)
            {
                modelCatalog.EnrichModelDefinitions(pc);
            }

            // Resolve fallback order: map template IDs → provider names, filter to known providers
            var fallbackOrder = request.FallbackOrder.Count > 0
                ? request.FallbackOrder
                    .Select(id => SetupHelpers.ResolveProviderName(id))
                    .Where(name => providerConfigs.Any(pc =>
                        string.Equals(pc.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .ToList()
                : providerConfigs.Select(pc => pc.Name).ToList();

            // Merge providers into the existing cortex.yml — preserves every other
            // section (tenants, channels, voice, memory, speech, etc.). Prior to
            // 2026-05-21 this call was a full-rewrite via SetupHelpers.GenerateYaml,
            // which silently nuked every other section on every save — including the
            // user's Discord/Voice channel configuration and tenants. The merge path
            // plus the backup-on-write safety net ensures that can't happen again.
            var existingYaml = File.Exists(cortexConfigPath)
                ? await File.ReadAllTextAsync(cortexConfigPath, CancellationToken.None).ConfigureAwait(false)
                : Cortex.Contained.Bridge.SetupHelpers.GenerateYaml(providerConfigs, fallbackOrder);
            var mergedYaml = Cortex.Contained.Bridge.Setup.CortexConfigMutator.UpdateLlmProviders(
                existingYaml, providerConfigs, fallbackOrder);
            Cortex.Contained.Bridge.Setup.CortexConfigStore.WriteWithBackup(cortexConfigPath, mergedYaml);

            // Update runtime config
            config.LlmProviders = providerConfigs;
            config.LlmProxy.FallbackOrder = fallbackOrder;

            // Re-push credentials to the agent so it immediately uses the updated providers
            if (tenantRouter.GetDefaultClient()!.IsConnected)
            {
                await worker.PushCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();
    }
}
