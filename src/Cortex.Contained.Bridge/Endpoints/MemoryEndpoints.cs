using Cortex.Contained.Bridge.Hosting;
using Cortex.Contained.Bridge.RemoteServices;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the memory management and memory-settings endpoints (<c>/api/memory*</c>). Memory CRUD is
/// proxied to the Agent Host over SignalR; settings are also persisted host-side. All require authorization.
/// </summary>
internal static class MemoryEndpoints
{
    private const string EmbeddingProviderSecretId = "embeddings-provider";
    private const string EmbeddingModel = "qwen3-embedding:0.6b";
    private const int EmbeddingDimensions = 1024;

    /// <summary>
    /// Maps the <c>/api/memory*</c> endpoints onto <paramref name="app"/>.
    /// </summary>
    /// <param name="app">The web application to register endpoints on.</param>
    /// <param name="cortexConfigPath">Absolute path to <c>cortex.yml</c> for settings persistence.</param>
    public static void MapMemoryEndpoints(this WebApplication app, string cortexConfigPath)
    {
        // --- Memory Management API ---

        app.MapGet("/api/memory", async (int? limit, int? offset, TenantRouter tenantRouter) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                var result = await tenantRouter.GetDefaultClient()!.ListMemoriesAsync(
                    limit ?? 50, offset ?? 0, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapGet("/api/memory/{memoryId}", async (string memoryId, TenantRouter tenantRouter) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                var item = await tenantRouter.GetDefaultClient()!.GetMemoryAsync(memoryId, CancellationToken.None).ConfigureAwait(false);
                return item is null
                    ? Results.Json(new { error = "Memory not found" }, statusCode: 404)
                    : Results.Ok(item);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapPost("/api/memory", async (MemoryCreateRequest request, TenantRouter tenantRouter) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            if (string.IsNullOrWhiteSpace(request.Content))
            {
                return Results.Json(new { error = "Content is required" }, statusCode: 400);
            }

            try
            {
                var item = await tenantRouter.GetDefaultClient()!.CreateMemoryAsync(request, CancellationToken.None).ConfigureAwait(false);
                return item is null
                    ? Results.Json(new { error = "Failed to create memory" }, statusCode: 500)
                    : Results.Ok(item);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapPut("/api/memory/{memoryId}", async (string memoryId, HttpContext ctx, TenantRouter tenantRouter) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            var body = await ctx.Request.ReadFromJsonAsync<MemoryUpdateBody>().ConfigureAwait(false);
            if (body is null)
            {
                return Results.Json(new { error = "Request body is required" }, statusCode: 400);
            }

            try
            {
                var request = new MemoryUpdateRequest
                {
                    MemoryId = memoryId,
                    Content = body.Content,
                    Title = body.Title,
                    Tags = body.Tags,
                };
                var item = await tenantRouter.GetDefaultClient()!.UpdateMemoryAsync(request, CancellationToken.None).ConfigureAwait(false);
                return item is null
                    ? Results.Json(new { error = "Memory not found" }, statusCode: 404)
                    : Results.Ok(item);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapDelete("/api/memory/{memoryId}", async (string memoryId, TenantRouter tenantRouter) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                var deleted = await tenantRouter.GetDefaultClient()!.DeleteMemoryAsync(memoryId, CancellationToken.None).ConfigureAwait(false);
                return deleted
                    ? Results.Ok(new { success = true })
                    : Results.Json(new { error = "Memory not found" }, statusCode: 404);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapPost("/api/memory/search", async (MemorySearchRequest request, TenantRouter tenantRouter) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return Results.Json(new { error = "Query is required" }, statusCode: 400);
            }

            try
            {
                var results = await tenantRouter.GetDefaultClient()!.SearchMemoriesAsync(request, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { results });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // --- Memory Settings API ---

        app.MapGet("/api/memory/settings", async (TenantRouter tenantRouter, BridgeConfig config) =>
        {
            // Try to get live values from agent; fall back to host-side config
            if (tenantRouter.GetDefaultClient()!.IsConnected)
            {
                try
                {
                    var liveConfig = await tenantRouter.GetDefaultClient()!.GetMemoryConfigAsync(CancellationToken.None).ConfigureAwait(false);
                    return Results.Ok(liveConfig);
                }
                catch
                {
                    // Fall through to host-side config
                }
            }

            // Return host-persisted values (from cortex.yml)
            var mem = config.Memory;
            return Results.Ok(new MemoryConfig
            {
                Enabled = mem.Enabled,
                DuplicateThreshold = mem.DuplicateThreshold,
                CompactionSimilarityThreshold = mem.CompactionSimilarityThreshold,
                CompactionEnabled = mem.CompactionEnabled,
                IdleCompactionEnabled = mem.IdleCompactionEnabled,
                IdleResetMinutes = mem.IdleResetMinutes,
                CompactionPreserveRecentTurns = mem.CompactionPreserveRecentTurns,
            });
        }).RequireAuthorization();

        // Built-in-memory master toggle. Persists to YAML, pushes the flag to the
        // agent (so the in-process gates flip live), and converges the embeddings sidecar.
        app.MapPost("/api/memory/toggle", async (
            HttpContext ctx,
            BridgeConfig config,
            CredentialsPusher credentialsPusher,
            Cortex.Contained.Bridge.Speech.EmbeddingsSidecarLifecycle embeddings,
            CancellationToken ct) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<MemoryToggleRequest>(ct).ConfigureAwait(false);
            if (body is null)
            {
                return Results.Json(new { error = "body is required" }, statusCode: 400);
            }

            MemoryToggleApply.Apply(config.Memory, body.Enabled);
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

            // Push the updated memory config (incl. Enabled) to every connected agent so the
            // tool gates + extraction/compaction skips flip live. Best-effort: PushMemorySettingsAsync
            // isolates per-tenant failures, and the agent re-syncs on next connect.
            await credentialsPusher.PushMemorySettingsAsync(ct).ConfigureAwait(false);

            // Converge the embeddings sidecar with the new flag (fire-and-forget so a slow
            // docker compose call never blocks the HTTP save).
            _ = Task.Run(() => embeddings.ReconcileAsync(config.Memory.Enabled, CancellationToken.None), CancellationToken.None);

            return Results.Ok(new { success = true, enabled = config.Memory.Enabled });
        }).RequireAuthorization();

        app.MapPut("/api/memory/settings", async (MemoryConfig memoryConfig, TenantRouter tenantRouter, BridgeConfig config) =>
        {
            if (!tenantRouter.GetDefaultClient()!.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            // Validate thresholds
            if (memoryConfig.DuplicateThreshold < 0f || memoryConfig.DuplicateThreshold > 1f)
            {
                return Results.Json(new { error = "DuplicateThreshold must be between 0.0 and 1.0" }, statusCode: 400);
            }

            if (memoryConfig.CompactionSimilarityThreshold < 0f || memoryConfig.CompactionSimilarityThreshold > 1f)
            {
                return Results.Json(new { error = "CompactionSimilarityThreshold must be between 0.0 and 1.0" }, statusCode: 400);
            }

            if (memoryConfig.IdleResetMinutes < 0 || memoryConfig.IdleResetMinutes > 1440)
            {
                return Results.Json(new { error = "IdleResetMinutes must be between 0 and 1440" }, statusCode: 400);
            }

            if (memoryConfig.CompactionPreserveRecentTurns < 0 || memoryConfig.CompactionPreserveRecentTurns > 100)
            {
                return Results.Json(new { error = "CompactionPreserveRecentTurns must be between 0 and 100" }, statusCode: 400);
            }

            try
            {
                // The settings page omits `enabled`, so the bound DTO defaults it to true. Force
                // the persisted master flag so saving unrelated settings never re-enables a
                // disabled memory subsystem (which would expose memory tools the stopped
                // embeddings sidecar cannot service).
                memoryConfig = MemoryToggleApply.WithPersistedEnabled(memoryConfig, config.Memory);
                await tenantRouter.GetDefaultClient()!.UpdateMemoryConfigAsync(memoryConfig, CancellationToken.None).ConfigureAwait(false);

                // Persist to cortex.yml so settings survive restarts
                config.Memory.DuplicateThreshold = memoryConfig.DuplicateThreshold;
                config.Memory.CompactionSimilarityThreshold = memoryConfig.CompactionSimilarityThreshold;
                config.Memory.CompactionEnabled = memoryConfig.CompactionEnabled;
                config.Memory.IdleCompactionEnabled = memoryConfig.IdleCompactionEnabled;
                config.Memory.IdleResetMinutes = memoryConfig.IdleResetMinutes;
                config.Memory.CompactionPreserveRecentTurns = memoryConfig.CompactionPreserveRecentTurns ?? config.Memory.CompactionPreserveRecentTurns;
                BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);

                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // --- Embedding Provider Settings API ---

        app.MapGet("/api/memory/embedding-provider", (
            BridgeConfig config,
            SecretManager secretManager,
            RemoteServiceResolver resolver) =>
        {
            return Results.Ok(BuildEmbeddingProviderDto(config, secretManager, resolver));
        }).RequireAuthorization();

        app.MapPost("/api/memory/embedding-provider/save", async (
            HttpContext ctx,
            BridgeConfig config,
            SecretManager secretManager,
            RemoteServiceResolver resolver,
            CredentialsPusher credentialsPusher,
            CancellationToken ct) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<EmbeddingProviderSaveBody>(ct).ConfigureAwait(false);
            if (body is null)
            {
                return Results.Json(new { error = "Request body is required" }, statusCode: 400);
            }

            config.Memory.EmbeddingEndpoint = string.IsNullOrWhiteSpace(body.Endpoint)
                ? null
                : body.Endpoint.Trim();

            // A present-but-empty apiKey means "clear the key"; storing an empty
            // string would attempt to DPAPI-encrypt "" (which throws). Remove instead.
            if (body.ApiKey is not null)
            {
                if (string.IsNullOrWhiteSpace(body.ApiKey))
                {
                    secretManager.RemoveApiKey(EmbeddingProviderSecretId);
                }
                else
                {
                    secretManager.StoreApiKey(EmbeddingProviderSecretId, body.ApiKey);
                }
            }

            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);
            await credentialsPusher.PushMemorySettingsAsync(ct).ConfigureAwait(false);

            return Results.Ok(BuildEmbeddingProviderDto(config, secretManager, resolver));
        }).RequireAuthorization();

        app.MapPost("/api/memory/embedding-provider/clear-key", async (
            BridgeConfig config,
            SecretManager secretManager,
            RemoteServiceResolver resolver,
            CredentialsPusher credentialsPusher,
            CancellationToken ct) =>
        {
            secretManager.RemoveApiKey(EmbeddingProviderSecretId);
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);
            await credentialsPusher.PushMemorySettingsAsync(ct).ConfigureAwait(false);

            return Results.Ok(BuildEmbeddingProviderDto(config, secretManager, resolver));
        }).RequireAuthorization();

        app.MapPost("/api/memory/embedding-provider/reset", async (
            BridgeConfig config,
            SecretManager secretManager,
            RemoteServiceResolver resolver,
            CredentialsPusher credentialsPusher,
            CancellationToken ct) =>
        {
            config.Memory.EmbeddingEndpoint = null;
            secretManager.RemoveApiKey(EmbeddingProviderSecretId);
            BridgeSettingsWriter.PersistSettingsToYaml(config, cortexConfigPath);
            await credentialsPusher.PushMemorySettingsAsync(ct).ConfigureAwait(false);

            return Results.Ok(BuildEmbeddingProviderDto(config, secretManager, resolver));
        }).RequireAuthorization();

        app.MapPost("/api/memory/embedding-provider/test", async (
            HttpContext ctx,
            BridgeConfig config,
            SecretManager secretManager,
            RemoteServiceResolver resolver,
            TenantRouter tenantRouter,
            CancellationToken ct) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<EmbeddingProviderTestBody>(ct).ConfigureAwait(false);

            var effectiveEndpoint = resolver.EffectiveEmbeddingEndpoint(
                body?.Endpoint ?? config.Memory.EmbeddingEndpoint);

            var apiKey = body?.ApiKey ?? secretManager.GetApiKey(EmbeddingProviderSecretId);

            // Probe runs on the AGENT, not the Bridge host: the Bridge can't resolve
            // Docker-internal names (http://embeddings:11434) that the agent uses at runtime.
            var client = tenantRouter.GetDefaultClient();
            if (client is null || !client.IsConnected)
            {
                return Results.Ok(new { ok = false, dim = (int?)null, error = "Agent not connected" });
            }

            try
            {
                var result = await client.TestEmbeddingEndpointAsync(effectiveEndpoint, apiKey, ct).ConfigureAwait(false);
                return Results.Ok(new { ok = result.Ok, dim = result.Dim, error = result.Error });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, dim = (int?)null, error = ex.Message });
            }
        }).RequireAuthorization();
    }

    private static object BuildEmbeddingProviderDto(
        BridgeConfig config,
        SecretManager secretManager,
        RemoteServiceResolver resolver)
    {
        var storedKey = secretManager.GetApiKey(EmbeddingProviderSecretId);
        return new
        {
            endpoint = resolver.EffectiveEmbeddingEndpoint(config.Memory.EmbeddingEndpoint),
            keySet = !string.IsNullOrEmpty(storedKey),
            model = EmbeddingModel,
            dimensions = EmbeddingDimensions,
            isDefault = resolver.IsEmbeddingDefault(config.Memory.EmbeddingEndpoint),
        };
    }

    private sealed class EmbeddingProviderSaveBody
    {
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
    }

    private sealed class EmbeddingProviderTestBody
    {
        public string? Endpoint { get; set; }
        public string? ApiKey { get; set; }
    }
}

