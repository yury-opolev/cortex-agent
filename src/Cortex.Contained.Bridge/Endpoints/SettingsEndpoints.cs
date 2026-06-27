using System.Globalization;
using Cortex.Contained.Bridge.Logging;
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the settings management endpoints (<c>/api/settings*</c>) for reading and updating
/// LLM provider configuration, fallback order, and model lists. All require authorization.
/// </summary>
internal static class SettingsEndpoints
{
    /// <summary>
    /// Maps the <c>/api/settings*</c> endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The web application to register endpoints on.</param>
    /// <param name="cortexConfigPath">Absolute path to <c>cortex.yml</c> for persistence.</param>
    public static void MapSettingsEndpoints(this WebApplication app, string cortexConfigPath)
    {
        // --- Settings API ---
        app.MapGet("/api/settings", (BridgeConfig config, TenantRouter tenantRouter, HttpContext ctx) =>
        {
            // Browsers have been observed serving stale /api/settings bodies after a
            // refresh-and-persist roundtrip, which makes the provider list look empty
            // when it's actually populated server-side. Force every response through.
            ctx.Response.Headers.CacheControl = "no-store";

            var providers = config.LlmProviders.Select(p => new
            {
                name = p.Name,
                api = p.Api,
                baseUrl = p.BaseUrl,
                apiKeyConfigured = !string.IsNullOrWhiteSpace(p.ApiKey),
                apiKeyHint = BridgeSettingsWriter.RedactApiKey(p.ApiKey),
                apiKeyFrom = p.ApiKeyFrom,
                models = p.Models,
                defaultModel = p.DefaultModel ?? (p.Models.Count > 0 ? p.Models[0] : null),
                memoryModel = p.MemoryModel,
            }).ToList();

            return Results.Ok(new
            {
                providers,
                fallbackOrder = config.LlmProxy.FallbackOrder,
                webUi = new { config.WebUi.Enabled, config.WebUi.Port, config.WebUi.BindAddress },
                agentConnected = tenantRouter.GetDefaultClient()!.IsConnected,
                channels = config.Channels,
                speech = new
                {
                    enabled = config.Speech.Enabled,
                    stt = new { config.Speech.Stt.Enabled, config.Speech.Stt.Engine, config.Speech.Stt.WhisperModelPath, config.Speech.Stt.Language },
                    tts = new { config.Speech.Tts.Enabled, config.Speech.Tts.Engine, config.Speech.Tts.KokoroVoice, config.Speech.Tts.KokoroModelPath, config.Speech.Tts.WindowsVoiceName, config.Speech.Tts.WindowsSpeechRate },
                    voiceId = new { config.Speech.VoiceId.Enabled },
                },
                memory = new { enabled = config.Memory.Enabled },
                maxSubagentRounds = config.MaxSubagentRounds,
            });
        }).RequireAuthorization();

        app.MapPost("/api/settings", async (
            SettingsUpdateRequest request,
            BridgeConfig config,
            ModelCatalog modelCatalog,
            Worker worker,
            TenantRouter tenantRouter,
            IHostEnvironment env,
            ILoggerFactory loggerFactory) =>
        {
            var changed = false;

            // Update fallback order
            if (request.FallbackOrder is not null)
            {
                // Validate all names reference real providers
                foreach (var name in request.FallbackOrder)
                {
                    if (!config.LlmProviders.Exists(p =>
                            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Results.Json(
                            new { error = string.Create(CultureInfo.InvariantCulture, $"Unknown provider: {name}") },
                            statusCode: 400);
                    }
                }

                config.LlmProxy.FallbackOrder = request.FallbackOrder;
                changed = true;
            }

            // Update per-provider model list (must run before default/memory model validation)
            if (request.ProviderModels is not null)
            {
                var settingsLogger = loggerFactory.CreateLogger("Cortex.Contained.Bridge.Settings.UpdateProviderModels");
                foreach (var (providerName, models) in request.ProviderModels)
                {
                    var provider = config.LlmProviders.Find(p =>
                        string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
                    if (provider is null)
                    {
                        return Results.Json(
                            new { error = string.Create(CultureInfo.InvariantCulture, $"Unknown provider: {providerName}") },
                            statusCode: 400);
                    }

                    if (models.Count == 0)
                    {
                        return Results.Json(
                            new { error = string.Create(CultureInfo.InvariantCulture, $"Model list cannot be empty for provider '{providerName}'") },
                            statusCode: 400);
                    }

                    if (settingsLogger.IsEnabled(LogLevel.Information))
                    {
                        var (added, removed) = SettingsDiff.ModelDiff(provider.Models, models);
#pragma warning disable CA1873 // guarded by IsEnabled above
                        BridgeLogMessages.LogModelsPersisted(
                            settingsLogger,
                            provider.Name,
                            provider.Models.Count,
                            models.Count,
                            string.Join(", ", added),
                            string.Join(", ", removed));
#pragma warning restore CA1873
                    }

                    provider.Models = models;

                    // Reset default/memory model if the currently selected model was removed
                    if (provider.DefaultModel is not null && !models.Contains(provider.DefaultModel))
                    {
                        provider.DefaultModel = models[0];
                    }

                    if (provider.MemoryModel is not null && !models.Contains(provider.MemoryModel))
                    {
                        provider.MemoryModel = null;
                    }

                    // Remove model definitions for models no longer in the list
                    provider.ModelDefinitions.RemoveAll(d => !models.Contains(d.Id));

                    // Enrich new models with metadata from the model catalog
                    modelCatalog.EnrichModelDefinitions(provider);

                    changed = true;
                }
            }

            // Update per-provider default model
            if (request.ProviderDefaultModels is not null)
            {
                foreach (var (providerName, defaultModel) in request.ProviderDefaultModels)
                {
                    var provider = config.LlmProviders.Find(p =>
                        string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
                    if (provider is null)
                    {
                        return Results.Json(
                            new { error = string.Create(CultureInfo.InvariantCulture, $"Unknown provider: {providerName}") },
                            statusCode: 400);
                    }

                    if (!string.IsNullOrWhiteSpace(defaultModel) && !provider.Models.Contains(defaultModel))
                    {
                        return Results.Json(
                            new { error = string.Create(CultureInfo.InvariantCulture, $"Model '{defaultModel}' not available for provider '{providerName}'") },
                            statusCode: 400);
                    }

                    provider.DefaultModel = string.IsNullOrWhiteSpace(defaultModel) ? null : defaultModel;
                    changed = true;
                }
            }

            // Update per-provider memory model
            if (request.ProviderMemoryModels is not null)
            {
                foreach (var (providerName, memoryModel) in request.ProviderMemoryModels)
                {
                    var provider = config.LlmProviders.Find(p =>
                        string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));
                    if (provider is null)
                    {
                        return Results.Json(
                            new { error = string.Create(CultureInfo.InvariantCulture, $"Unknown provider: {providerName}") },
                            statusCode: 400);
                    }

                    // Empty string = clear (fall back to default model)
                    provider.MemoryModel = string.IsNullOrWhiteSpace(memoryModel) ? null : memoryModel;
                    changed = true;
                }
            }

            // Update max sub-agent rounds (takes effect on agent container restart)
            if (request.MaxSubagentRounds.HasValue)
            {
                config.MaxSubagentRounds = request.MaxSubagentRounds.Value;
                changed = true;
            }

            // Persist to cortex.yml if anything changed
            if (changed)
            {
                BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

                // Re-push credentials to the agent so it picks up the new
                // fallback order immediately. The agent derives its default
                // model from the first provider in the ordered list.
                await worker.PushCredentialsAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // Refresh available models for an existing provider (uses stored API key — never exposed to frontend)
        app.MapPost("/api/settings/refresh-models", async (
            RefreshModelsRequest request,
            BridgeConfig config,
            ModelCatalog modelCatalog,
            Worker worker,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory) =>
        {
            var provider = config.LlmProviders.Find(p =>
                string.Equals(p.Name, request.ProviderName, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                return Results.Json(
                    new { error = string.Create(CultureInfo.InvariantCulture, $"Unknown provider: {request.ProviderName}") },
                    statusCode: 400);
            }

            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                return Results.Json(new { error = "No API key configured for this provider" }, statusCode: 400);
            }

            var refreshLogger = loggerFactory.CreateLogger("Cortex.Contained.Bridge.Settings.RefreshModels");
            var currentIds = provider.Models.ToList();

            try
            {
                // Force-refresh model metadata from models.dev so enrichment picks up
                // any context window / max output token changes immediately.
                await modelCatalog.RefreshAsync(CancellationToken.None).ConfigureAwait(false);

                var apiKey = provider.ApiKey;
                var isOAuth = string.Equals(provider.TokenType, "oauth", StringComparison.OrdinalIgnoreCase);
                var tokenType = isOAuth ? "oauth" : null;

                using var httpClient = httpFactory.CreateClient();

                List<AvailableModel> models;
                try
                {
                    models = await SetupHelpers.FetchAvailableModelsAsync(
                        provider.Name, apiKey, httpClient, tokenType).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (
                    ex.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    && isOAuth
                    && !string.IsNullOrEmpty(provider.RefreshToken))
                {
                    // OAuth token expired — refresh and retry once, same pattern as the Agent Host
                    var refreshResult = await worker.HandleTokenRefreshRequestAsync(
                        provider.Name, CancellationToken.None).ConfigureAwait(false);
                    if (!refreshResult.Success || string.IsNullOrEmpty(refreshResult.AccessToken))
                    {
                        return Results.Json(
                            new { error = $"OAuth token expired and refresh failed: {refreshResult.Error}" },
                            statusCode: 401);
                    }

                    models = await SetupHelpers.FetchAvailableModelsAsync(
                        provider.Name, refreshResult.AccessToken, httpClient, tokenType).ConfigureAwait(false);
                }

                var newIds = models.Select(m => m.Id).ToList();
                if (refreshLogger.IsEnabled(LogLevel.Information))
                {
                    var (added, removed) = SettingsDiff.ModelDiff(currentIds, newIds);
#pragma warning disable CA1873 // guarded by IsEnabled above
                    BridgeLogMessages.LogModelsRefreshed(
                        refreshLogger,
                        provider.Name,
                        currentIds.Count,
                        newIds.Count,
                        string.Join(", ", added),
                        string.Join(", ", removed));
#pragma warning restore CA1873
                }

                return Results.Ok(new { models });
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
    }
}
