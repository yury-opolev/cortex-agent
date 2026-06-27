using System.Globalization;
using System.Text.Json.Serialization;
using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Contracts;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Tenants;

/// <summary>
/// Registers tenant management API endpoints on the Bridge.
/// </summary>
public static class TenantEndpoints
{
    /// <summary>How long a setup code remains valid before expiring.</summary>
    public static readonly TimeSpan SetupCodeExpiry = TimeSpan.FromHours(24);

    /// <summary>Maps all <c>/api/tenants</c> endpoints.</summary>
    public static void MapTenantEndpoints(this WebApplication app)
    {
        // ── Tenant CRUD ──────────────────────────────────────────────

        // GET /api/tenants — list all tenants with status and health
        app.MapGet("/api/tenants", (TenantRouter router, TenantHealthService healthService, TenantRegistry registry) =>
        {
            var statuses = router.GetAllStatuses();
            var healthStates = healthService.GetAllHealthStates();

            // Merge health state into status response
            var result = statuses.Select(s =>
            {
                TenantHealthState? health = null;
                healthStates?.TryGetValue(s.TenantId, out health);
                var config = registry.GetTenant(s.TenantId);
                return new
                {
                    s.TenantId,
                    s.Endpoint,
                    s.IsDefault,
                    s.Enabled,
                    s.Connected,
                    s.Port,
                    s.ImageVersion,
                    s.DiscordUserId,
                    s.DiscordUsername,
                    s.HasSetupCode,
                    s.SetupCodeExpiresAt,
                    s.ApiEnabled,
                    VoiceGender = config?.VoiceGender ?? "female",
                    config?.DiscordGuildId,
                    config?.DiscordVoiceChannelId,
                    config?.VoiceGreeting,
                    Status = health?.Status.ToString() ?? (s.Connected ? "Connected" : "Unknown"),
                    health?.LastPingAt,
                    health?.LastActivityAt,
                    MessageCount = health?.MessageCount ?? 0,
                    health?.AgentVersion,
                    health?.LastError,
                };
            });

            return Results.Ok(result);
        }).RequireAuthorization();

        // POST /api/tenants — create a new tenant
        app.MapPost("/api/tenants", (CreateTenantRequest request, TenantRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(request.TenantId))
            {
                return Results.BadRequest(new { error = "tenantId is required" });
            }

            var config = new TenantConfig
            {
                Endpoint = request.Endpoint ?? string.Empty,
                Default = request.Default,
                Enabled = request.Enabled,
                Port = request.Port,
                Channels = request.Channels ?? [],
                ApiEnabled = request.ApiEnabled,
                IdleTimeoutMinutes = request.IdleTimeoutMinutes,
            };

            try
            {
                registry.AddTenant(request.TenantId, config);
                return Results.Created($"/api/tenants/{request.TenantId}", config);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // PUT /api/tenants/{tenantId} — update tenant config
        app.MapPut("/api/tenants/{tenantId}", async (
            string tenantId,
            UpdateTenantRequest request,
            TenantRegistry registry,
            [FromServices] DiscordChannel? discordChannel,
            [FromServices] DiscordChannelOptions? discordOptions) =>
        {
            var existing = registry.GetTenant(tenantId);
            if (existing is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found" });
            }

            // Merge: only update fields that are provided
            if (request.Endpoint is not null)
            {
                existing.Endpoint = request.Endpoint;
            }

            if (request.Default.HasValue)
            {
                existing.Default = request.Default.Value;
            }

            if (request.Enabled.HasValue)
            {
                existing.Enabled = request.Enabled.Value;
            }

            if (request.Port.HasValue)
            {
                existing.Port = request.Port.Value;
            }

            if (request.Channels is not null)
            {
                existing.Channels = request.Channels;
            }

            if (request.ApiEnabled.HasValue)
            {
                existing.ApiEnabled = request.ApiEnabled.Value;
            }

            if (request.IdleTimeoutMinutes.HasValue)
            {
                existing.IdleTimeoutMinutes = request.IdleTimeoutMinutes.Value;
            }

            if (request.VoiceGender is not null)
            {
                existing.VoiceGender = request.VoiceGender;
            }

            if (request.DiscordGuildId is not null)
            {
                existing.DiscordGuildId = request.DiscordGuildId.Length > 0 ? request.DiscordGuildId : null;
            }

            if (request.DiscordVoiceChannelId is not null)
            {
                existing.DiscordVoiceChannelId = request.DiscordVoiceChannelId.Length > 0 ? request.DiscordVoiceChannelId : null;
            }

            if (request.VoiceGreeting is not null)
            {
                existing.VoiceGreeting = request.VoiceGreeting.Length > 0 ? request.VoiceGreeting : null;
            }

            registry.UpdateTenant(tenantId, existing);

            // Trigger voice handler reconciliation if Discord channel is available
            if (discordChannel is not null && discordOptions is not null)
            {
                var voiceConfigs = TenantEndpoints.BuildVoiceHandlerConfigs(registry, discordOptions);
                await discordChannel.ReconcileVoiceHandlers(voiceConfigs).ConfigureAwait(false);
            }

            return Results.Ok(existing);
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId} — delete tenant (cannot delete default tenant)
        app.MapDelete("/api/tenants/{tenantId}", async (string tenantId, TenantRegistry registry, TenantRouter router) =>
        {
            // Prevent deleting the default tenant
            var tenant = registry.GetTenant(tenantId);
            if (tenant is not null && tenant.Default)
            {
                return Results.BadRequest(new { error = "Cannot delete the default tenant. Use the reset endpoint to clear its data instead." });
            }

            try
            {
                await router.DisconnectTenantAsync(tenantId).ConfigureAwait(false);
                registry.RemoveTenant(tenantId);
                return Results.Ok(new { deleted = tenantId });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/default — set as default tenant
        app.MapPost("/api/tenants/{tenantId}/default", (string tenantId, TenantRegistry registry) =>
        {
            try
            {
                registry.SetDefault(tenantId);
                return Results.Ok(new { defaultTenant = tenantId });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // ── Personality ───────────────────────────────────────────────

        // GET /api/tenants/{tenantId}/personality — get live personality from agent
        app.MapGet("/api/tenants/{tenantId}/personality", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                var personality = await client.GetPersonalityAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { personality });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // PUT /api/tenants/{tenantId}/personality — set personality on agent
        app.MapPut("/api/tenants/{tenantId}/personality", async (
            HttpContext ctx, string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            var body = await ctx.Request.ReadFromJsonAsync<PersonalityUpdateRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.Personality))
            {
                return Results.BadRequest(new { error = "Personality text is required" });
            }

            try
            {
                await client.SetPersonalityAsync(body.Personality.Trim(), CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/personality — reset personality to default
        app.MapDelete("/api/tenants/{tenantId}/personality", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                await client.SetPersonalityAsync(PersonalityDefaults.DefaultPersonality, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { success = true, personality = PersonalityDefaults.DefaultPersonality });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // ── Self-Notes ────────────────────────────────────────────────

        // GET /api/tenants/{tenantId}/self-notes — get live self-notes from agent
        app.MapGet("/api/tenants/{tenantId}/self-notes", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.StatusCode(503);
            }

            var selfNotes = await client.GetSelfNotesAsync(CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new { selfNotes });
        }).RequireAuthorization();

        // PUT /api/tenants/{tenantId}/self-notes — set self-notes on agent
        app.MapPut("/api/tenants/{tenantId}/self-notes", async (
            HttpContext ctx, string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.StatusCode(503);
            }

            var body = await ctx.Request.ReadFromJsonAsync<SelfNotesUpdateRequest>().ConfigureAwait(false);
            if (body is null || string.IsNullOrWhiteSpace(body.SelfNotes))
            {
                return Results.BadRequest(new { error = "selfNotes is required" });
            }

            await client.SetSelfNotesAsync(body.SelfNotes.Trim(), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new { success = true });
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/self-notes — reset self-notes to default
        app.MapDelete("/api/tenants/{tenantId}/self-notes", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.StatusCode(503);
            }

            var selfNotes = await client.ResetSelfNotesAsync(CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new { success = true, selfNotes });
        }).RequireAuthorization();

        // ── Voice Identification (Phase 3) ───────────────────────────

        // GET /api/tenants/{tenantId}/voice-id — current snapshot
        app.MapGet("/api/tenants/{tenantId}/voice-id", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                var snapshot = await client.GetVoiceprintAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new
                {
                    state = snapshot?.StateName ?? "Unknown",
                    featureEnabled = snapshot?.FeatureEnabled ?? true,
                    embeddingDim = snapshot?.EmbeddingDim ?? 0,
                    modelId = snapshot?.ModelId,
                    thresholdOverride = snapshot?.ThresholdOverride,
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // PUT /api/tenants/{tenantId}/voice-id — toggle feature or set threshold override
        app.MapPut("/api/tenants/{tenantId}/voice-id", async (
            HttpContext ctx, string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            var body = await ctx.Request.ReadFromJsonAsync<VoiceIdUpdateRequest>().ConfigureAwait(false);
            if (body is null)
            {
                return Results.BadRequest(new { error = "Request body is required" });
            }

            try
            {
                if (body.FeatureEnabled is { } enabled)
                {
                    await client.SetVoiceFeatureEnabledAsync(tenantId, enabled, CancellationToken.None).ConfigureAwait(false);
                }
                if (body.ThresholdOverridePresent)
                {
                    await client.SetVoiceThresholdOverrideAsync(tenantId, body.ThresholdOverride, CancellationToken.None).ConfigureAwait(false);
                }
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/voice-id — wipe voiceprint, set state Declined
        app.MapDelete("/api/tenants/{tenantId}/voice-id", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                await client.ResetVoiceEnrollmentAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // GET /api/voice-id/metrics — Bridge-side gate outcome counters
        app.MapGet("/api/voice-id/metrics", (
            Cortex.Contained.Speech.SpeakerId.VerificationMetrics metrics) =>
        {
            var snapshot = metrics.Snapshot();
            return Results.Ok(snapshot);
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/voice-id/start-enrollment — admin arms enrollment
        app.MapPost("/api/tenants/{tenantId}/voice-id/start-enrollment", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.Json(new { error = "Agent not connected" }, statusCode: 503);
            }

            try
            {
                var error = await client.StartVoiceEnrollmentAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
                if (error is not null)
                {
                    return Results.Json(new { error }, statusCode: 409);
                }
                return Results.Ok(new { success = true });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        }).RequireAuthorization();

        // ── Discord Pairing ──────────────────────────────────────────

        // POST /api/tenants/{tenantId}/setup-code — generate a pairing code
        app.MapPost("/api/tenants/{tenantId}/setup-code", (string tenantId, TenantRegistry registry) =>
        {
            try
            {
                var code = registry.GenerateSetupCode(tenantId, SetupCodeExpiry);
                var tenant = registry.GetTenant(tenantId);
                return Results.Ok(new
                {
                    code,
                    expiresAt = tenant?.SetupCodeExpiresAt ?? 0,
                    expiryHours = SetupCodeExpiry.TotalHours,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/setup-code — revoke active pairing code
        app.MapDelete("/api/tenants/{tenantId}/setup-code", (string tenantId, TenantRegistry registry) =>
        {
            try
            {
                registry.RevokeSetupCode(tenantId);
                return Results.Ok(new { revoked = true });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/discord — unlink Discord user from tenant
        app.MapDelete("/api/tenants/{tenantId}/discord", (string tenantId, TenantRegistry registry) =>
        {
            try
            {
                registry.UnlinkDiscordUser(tenantId);
                return Results.Ok(new { unlinked = true });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        }).RequireAuthorization();

        // ── API Key Management (admin auth) ─────────────────────────

        // POST /api/tenants/{tenantId}/api-key — generate or regenerate an API key
        app.MapPost("/api/tenants/{tenantId}/api-key", (
            string tenantId, TenantRegistry registry, SecretManager secrets) =>
        {
            var tenant = registry.GetTenant(tenantId);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found" });
            }

            var key = secrets.GenerateTenantApiKey(tenantId);
            return Results.Ok(new { apiKey = key });
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/api-key — revoke API key
        app.MapDelete("/api/tenants/{tenantId}/api-key", (
            string tenantId, TenantRegistry registry, SecretManager secrets) =>
        {
            var tenant = registry.GetTenant(tenantId);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found" });
            }

            secrets.RevokeTenantApiKey(tenantId);
            return Results.Ok(new { revoked = true });
        }).RequireAuthorization();

        // ── API Channel (per-tenant API key auth) ────────────────────

        // POST /api/tenants/{tenantId}/message — send message via API channel
        // Auth: X-Api-Key header (per-tenant key, NOT Bridge session auth)
        app.MapPost("/api/tenants/{tenantId}/message", async (
            HttpContext ctx,
            string tenantId,
            SendMessageRequest request,
            TenantRegistry registry,
            TenantRouter router,
            SecretManager secrets) =>
        {
            // Authenticate via per-tenant API key
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey))
            {
                return Results.Json(new { error = "Missing X-Api-Key header" }, statusCode: 401);
            }

            var tenant = registry.GetTenant(tenantId);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found" });
            }

            if (!tenant.ApiEnabled)
            {
                return Results.Json(new { error = "API channel is not enabled for this tenant" }, statusCode: 403);
            }

            var storedKey = secrets.GetTenantApiKey(tenantId);
            if (storedKey is null || !string.Equals(storedKey, apiKey, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);
            }

            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.StatusCode(503);
            }

            try
            {
                var conversationId = $"api-{tenantId}";
                var hubMessage = new HubInboundMessage
                {
                    ConversationId = conversationId,
                    ChannelId = $"api-{tenantId}",
                    SenderIdHash = "api",
                    Text = request.Text,
                    Timestamp = DateTimeOffset.UtcNow,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                };

                // Start waiting BEFORE sending, so we don't miss the response
                var responseTask = client.WaitForResponseAsync(
                    conversationId, TimeSpan.FromMinutes(5), CancellationToken.None);

                var sendResult = await client.SendMessageAsync(hubMessage, CancellationToken.None)
                    .ConfigureAwait(false);

                if (!sendResult.Accepted)
                {
                    return Results.Json(new { error = sendResult.RejectionReason ?? "Message rejected" }, statusCode: 422);
                }

                var response = await responseTask.ConfigureAwait(false);

                return Results.Ok(new
                {
                    conversationId,
                    response = response.FullText,
                    messageId = response.MessageId,
                    timestamp = response.Timestamp,
                });
            }
            catch (TimeoutException)
            {
                return Results.Json(new { error = "Agent response timed out" }, statusCode: 504);
            }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("Agent error", StringComparison.Ordinal))
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 500);
            }
        });

        // GET /api/tenants/{tenantId}/messages — get API channel message history
        // Auth: X-Api-Key header
        app.MapGet("/api/tenants/{tenantId}/messages", async (
            HttpContext ctx,
            string tenantId,
            int? limit,
            int? offset,
            TenantRegistry registry,
            TenantRouter router,
            SecretManager secrets) =>
        {
            var apiKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrEmpty(apiKey))
            {
                return Results.Json(new { error = "Missing X-Api-Key header" }, statusCode: 401);
            }

            var tenant = registry.GetTenant(tenantId);
            if (tenant is null)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not found" });
            }

            if (!tenant.ApiEnabled)
            {
                return Results.Json(new { error = "API channel is not enabled for this tenant" }, statusCode: 403);
            }

            var storedKey = secrets.GetTenantApiKey(tenantId);
            if (storedKey is null || !string.Equals(storedKey, apiKey, StringComparison.Ordinal))
            {
                return Results.Json(new { error = "Invalid API key" }, statusCode: 401);
            }

            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.StatusCode(503);
            }

            try
            {
                var channelId = $"api-{tenantId}";
                var result = await client.GetMessagesAsync(channelId, limit ?? 50, offset ?? 0, CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        });

        // ── Tenant interaction (admin auth — data access) ────────────

        // GET /api/tenants/{tenantId}/history — list conversations
        // Optional channelId filter narrows results to a single channel.
        app.MapGet("/api/tenants/{tenantId}/history", async (
            string tenantId, string? channelId, int? limit, int? offset, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            var filter = string.IsNullOrWhiteSpace(channelId) ? null : channelId;

            try
            {
                var result = await client.GetConversationsAsync(filter, limit ?? 50, offset ?? 0, CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // GET /api/tenants/{tenantId}/history/{conversationId} — get messages
        app.MapGet("/api/tenants/{tenantId}/history/{conversationId}", async (
            string tenantId, string conversationId, int? limit, int? offset, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.GetMessagesAsync(conversationId, limit ?? 100, offset ?? 0, CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/history — delete messages older than a date
        app.MapDelete("/api/tenants/{tenantId}/history", async (
            string tenantId, string? olderThan, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                int deleted;
                if (!string.IsNullOrWhiteSpace(olderThan)
                    && DateTimeOffset.TryParse(olderThan, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var olderThanTs))
                {
                    deleted = await client.DeleteMessagesOlderThanAsync(olderThanTs, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    deleted = await client.ClearAllMessagesAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return Results.Ok(new { deletedCount = deleted });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // GET /api/tenants/{tenantId}/channels — list distinct channels with counts and last activity
        app.MapGet("/api/tenants/{tenantId}/channels", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var summaries = await client.GetChannelSummariesAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(summaries);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/channels/{channelId}/history — clear messages for a single channel
        app.MapDelete("/api/tenants/{tenantId}/channels/{channelId}/history", async (
            string tenantId, string channelId, string? olderThan, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            if (!ChannelHistoryEndpoints.TryParseChannelId(channelId, out var decodedChannelId))
            {
                return Results.BadRequest(new { error = "channelId is required" });
            }

            if (!ChannelHistoryEndpoints.TryParseOlderThan(olderThan, out var cutoff))
            {
                return Results.BadRequest(new { error = "olderThan must be an ISO 8601 timestamp" });
            }

            try
            {
                await client.DeleteChannelMessagesOlderThanAsync(decodedChannelId!, cutoff, CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.NoContent();
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();


        // GET /api/tenants/{tenantId}/memories — list memories
        app.MapGet("/api/tenants/{tenantId}/memories", async (
            string tenantId, int? limit, int? offset, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ListMemoriesAsync(limit ?? 100, offset ?? 0, CancellationToken.None)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/compact — flush extraction + compact conversation
        app.MapPost("/api/tenants/{tenantId}/compact", async (
            string tenantId, string? channelId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            if (string.IsNullOrWhiteSpace(channelId))
            {
                return Results.BadRequest(new { error = "channelId query parameter is required" });
            }

            try
            {
                var result = await client.CompactConversationAsync(channelId, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/compact-memories — run memory dedup sweep
        app.MapPost("/api/tenants/{tenantId}/compact-memories", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.CompactMemoriesAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/reset-session — clear and re-seed session from history
        app.MapPost("/api/tenants/{tenantId}/reset-session", async (
            string tenantId, string? channelId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            if (string.IsNullOrWhiteSpace(channelId))
            {
                return Results.BadRequest(new { error = "channelId query parameter is required" });
            }

            try
            {
                await client.ResetAndReseedSessionAsync(channelId, CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { success = true, channelId });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // ── Export/Import ────────────────────────────────────────────

        // GET /api/tenants/{tenantId}/export — export all agent data
        app.MapGet("/api/tenants/{tenantId}/export", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ExportAllAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch (TimeoutException)
            {
                return Results.StatusCode(504);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // GET /api/tenants/{tenantId}/export/memories — export memories
        app.MapGet("/api/tenants/{tenantId}/export/memories", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ExportMemoriesAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // GET /api/tenants/{tenantId}/export/messages — export messages
        app.MapGet("/api/tenants/{tenantId}/export/messages", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ExportMessagesAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // GET /api/tenants/{tenantId}/export/tasks — export tasks
        app.MapGet("/api/tenants/{tenantId}/export/tasks", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ExportTasksAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/import — import full bundle
        app.MapPost("/api/tenants/{tenantId}/import", async (
            string tenantId, ExportBundle bundle, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ImportAllAsync(bundle, CancellationToken.None).ConfigureAwait(false);
                return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
            }
            catch (TimeoutException)
            {
                return Results.StatusCode(504);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/import/memories — import memories
        app.MapPost("/api/tenants/{tenantId}/import/memories", async (
            string tenantId, ImportMemoriesRequest request, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ImportMemoriesAsync(request, CancellationToken.None).ConfigureAwait(false);
                return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/import/messages — import messages
        app.MapPost("/api/tenants/{tenantId}/import/messages", async (
            string tenantId, ImportMessagesRequest request, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ImportMessagesAsync(request, CancellationToken.None).ConfigureAwait(false);
                return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/import/tasks — import tasks
        app.MapPost("/api/tenants/{tenantId}/import/tasks", async (
            string tenantId, ImportTasksRequest request, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                var result = await client.ImportTasksAsync(request, CancellationToken.None).ConfigureAwait(false);
                return result.Success ? Results.Ok(result) : Results.UnprocessableEntity(result);
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // ── Data clearing ────────────────────────────────────────────

        // DELETE /api/tenants/{tenantId}/messages — clear all messages
        app.MapDelete("/api/tenants/{tenantId}/messages", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                await client.ClearAllMessagesAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { cleared = "messages" });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // DELETE /api/tenants/{tenantId}/memories — clear all memories
        app.MapDelete("/api/tenants/{tenantId}/memories", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                await client.ClearAllMemoriesAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { cleared = "memories" });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();

        // POST /api/tenants/{tenantId}/reset — clear everything
        app.MapPost("/api/tenants/{tenantId}/reset", async (
            string tenantId, TenantRouter router) =>
        {
            var client = router.GetClient(tenantId);
            if (client is null || !client.IsConnected)
            {
                return Results.NotFound(new { error = $"Tenant '{tenantId}' not available" });
            }

            try
            {
                await client.ClearAllAsync(CancellationToken.None).ConfigureAwait(false);
                return Results.Ok(new { cleared = "all" });
            }
            catch
            {
                return Results.StatusCode(503);
            }
        }).RequireAuthorization();
    }

    /// <summary>
    /// Build voice handler configs from all tenants in the registry.
    /// Used both at startup and when tenant config changes.
    /// </summary>
    internal static Dictionary<string, VoiceHandlerConfig> BuildVoiceHandlerConfigs(
        TenantRegistry registry,
        DiscordChannelOptions globalOptions,
        Cortex.Contained.Contracts.Recording.IRecordingController? recorder = null,
        Cortex.Contained.Speech.SpeakerId.ISpeakerVerifier? speakerVerifier = null,
        Cortex.Contained.Speech.SpeakerId.VerificationMetrics? verificationMetrics = null,
        Cortex.Contained.Speech.SpeakerId.ISpeakerEmbedder? speakerEmbedder = null,
        Func<string, float[], string, Task>? submitVoiceprintAsync = null,
        Cortex.Contained.Speech.Tts.ILanguageDetector? languageDetector = null,
        Cortex.Contained.Speech.Tts.ChannelLanguageStore? languageStore = null,
        Cortex.Contained.Speech.Tts.LanguageSwitchThresholds? languageSwitchThresholds = null)
    {
        var configs = new Dictionary<string, VoiceHandlerConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var (tenantId, tenant) in registry.GetAll())
        {
            if (!tenant.Enabled
                || string.IsNullOrWhiteSpace(tenant.DiscordVoiceChannelId)
                || string.IsNullOrWhiteSpace(tenant.DiscordGuildId)
                || string.IsNullOrWhiteSpace(tenant.DiscordUserId))
            {
                continue;
            }

            if (!ulong.TryParse(tenant.DiscordGuildId, out var guildId)
                || !ulong.TryParse(tenant.DiscordVoiceChannelId, out var voiceChannelId)
                || !ulong.TryParse(tenant.DiscordUserId, out var linkedUserId))
            {
                continue;
            }

            configs[tenantId] = new VoiceHandlerConfig
            {
                TenantId = tenantId,
                GuildId = guildId,
                VoiceChannelId = voiceChannelId,
                VoiceGreeting = tenant.VoiceGreeting,
                VoiceGender = ParseGender(tenant.VoiceGender),
                LinkedUserId = linkedUserId,
                SilenceTimeoutMs = globalOptions.SilenceTimeoutMs,
                EnableBargeIn = globalOptions.EnableBargeIn,
                UseStreamingStt = globalOptions.UseStreamingStt,
                UseTurnDetector = globalOptions.UseTurnDetector,
                OutputGain = globalOptions.OutputGain,
                BargeInOnsetGuardMs = globalOptions.BargeInOnsetGuardMs,
                BargeInClassifierMode = globalOptions.BargeInClassifierMode,
                Recorder = recorder,
                SpeakerVerifier = speakerVerifier,
                VerificationMetrics = verificationMetrics,
                SpeakerEmbedder = speakerEmbedder,
                SubmitVoiceprintAsync = submitVoiceprintAsync,
                LanguageDetector = languageDetector,
                LanguageStore = languageStore,
                LanguageSwitchThresholds = languageSwitchThresholds ?? Cortex.Contained.Speech.Tts.LanguageSwitchThresholds.Default,
            };
        }

        return configs;
    }

    private static Cortex.Contained.Speech.VoiceGender ParseGender(string? s) =>
        string.Equals(s, "male", StringComparison.OrdinalIgnoreCase)
            ? Cortex.Contained.Speech.VoiceGender.Male
            : Cortex.Contained.Speech.VoiceGender.Female;
}

// ── Request DTOs ──────────────────────────────────────────────────────

public sealed record CreateTenantRequest
{
    public required string TenantId { get; init; }
    public string? Endpoint { get; init; }
    public bool Default { get; init; }
    public bool Enabled { get; init; } = true;
    public int Port { get; init; }
    public List<string>? Channels { get; init; }
    public bool ApiEnabled { get; init; }
    public int IdleTimeoutMinutes { get; init; }
}

public sealed record UpdateTenantRequest
{
    public string? Endpoint { get; init; }
    public bool? Default { get; init; }
    public bool? Enabled { get; init; }
    public int? Port { get; init; }
    public List<string>? Channels { get; init; }
    public bool? ApiEnabled { get; init; }
    public int? IdleTimeoutMinutes { get; init; }
    public string? VoiceGender { get; init; }
    public string? DiscordGuildId { get; init; }
    public string? DiscordVoiceChannelId { get; init; }
    public string? VoiceGreeting { get; init; }
}

public sealed record SendMessageRequest
{
    public required string Text { get; init; }
    public string? ChannelId { get; init; }
}

public sealed class PersonalityUpdateRequest
{
    [JsonPropertyName("personality")]
    public string? Personality { get; set; }
}

public sealed class SelfNotesUpdateRequest
{
    [JsonPropertyName("selfNotes")]
    public string? SelfNotes { get; set; }
}

public sealed class VoiceIdUpdateRequest
{
    [JsonPropertyName("featureEnabled")]
    public bool? FeatureEnabled { get; set; }

    /// <summary>
    /// Per-tenant cosine threshold override. Pass null with
    /// <see cref="ThresholdOverridePresent"/> true to clear the override and
    /// fall back to the default.
    /// </summary>
    [JsonPropertyName("thresholdOverride")]
    public float? ThresholdOverride { get; set; }

    /// <summary>
    /// True when <see cref="ThresholdOverride"/> was sent in the body
    /// (including null to clear). Tracked separately from the nullable so
    /// we can distinguish "not sent" from "sent as null".
    /// </summary>
    [JsonPropertyName("thresholdOverridePresent")]
    public bool ThresholdOverridePresent { get; set; }
}
