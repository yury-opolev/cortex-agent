using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Security;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Cortex.Contained.Contracts.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Bridge.Storage;

/// <summary>Body of <c>POST /api/tenants/{tenantId}/storage/encryption/enable</c>.</summary>
public sealed record StorageEnableRequest(string? Password, string? Confirm, bool EraseEvaluationArtifacts = true, bool AcceptNoRecoveryKit = false);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/storage/recovery-kit</c>.</summary>
public sealed record RecoveryKitExportRequest(string? Password, string? Passphrase);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/storage/recovery-kit/restore</c>.</summary>
public sealed record RecoveryKitRestoreRequest(string? Password, string? Passphrase, string? Kit, bool ReplaceExistingKey = false);

/// <summary>
/// The Storage page API (all require authorization; the sensitive ones also the Bridge password again):
/// <c>/api/tenants/{tenantId}/storage/status</c> (GET), <c>/storage/encryption/preview</c> (POST),
/// <c>/storage/encryption/enable</c> (POST, one-way), <c>/storage/recovery-kit</c> (POST, download) and
/// <c>/storage/recovery-kit/restore</c> (POST). Keys never appear in responses or logs, only fingerprints.
/// The handlers are internal static seams so they are tested without a web host.
/// </summary>
internal static class StorageEndpoints
{
    /// <summary>What the owner types to confirm enabling encryption.</summary>
    internal const string EnableConfirmation = "ENCRYPT";

    /// <summary>Maps the Storage endpoints onto <paramref name="app"/>.</summary>
    public static void MapStorageEndpoints(this WebApplication app, string cortexDataDir)
    {
        app.MapGet("/api/tenants/{tenantId}/storage/status", (string tenantId, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, CancellationToken ct) =>
            GetStatusAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, cortexDataDir, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/storage/encryption/preview", (string tenantId, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, CancellationToken ct) =>
            PreviewAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/storage/encryption/enable", (string tenantId, StorageEnableRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, [FromServices] SessionManager sessions, CancellationToken ct) =>
            EnableAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/storage/recovery-kit", async (string tenantId, RecoveryKitExportRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, [FromServices] SessionManager sessions, [FromServices] TimeProvider time, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ExportKitAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, Reauthenticate(sessions, http), body, time, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/storage/recovery-kit/restore", (string tenantId, RecoveryKitRestoreRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SecretManager secrets, [FromServices] SessionManager sessions, CancellationToken ct) =>
            RestoreKitAsync(tenantId, HubStorageAgentClient.For(router.GetClient(tenantId)), secrets, Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
    }

    // Failed re-authentications are counted per session; the third invalidates it (see SessionManager.VerifyPassword).
    private static Func<string?, bool> Reauthenticate(SessionManager sessions, HttpContext http) =>
        password => sessions.VerifyPassword(password, http.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var token) ? token : null);

    internal static async Task<IResult> GetStatusAsync(string tenantId, IStorageAgentClient? agent, SecretManager secrets, string cortexDataDir, CancellationToken ct)
    {
        var bridge = KeyView(secrets, tenantId);
        var bridgeStores = BridgeStorageReport.Scan(cortexDataDir);
        if (agent is null)
        {
            return Results.Json(new { connected = false, bridge, bridgeStores }, statusCode: 503);
        }

        return await RecallEndpoints.RelayAsync(
            () => agent.GetStatusAsync(ct),
            status => Results.Ok(new { connected = true, agent = status, bridge, bridgeStores })).ConfigureAwait(false);
    }

    internal static async Task<IResult> PreviewAsync(string tenantId, IStorageAgentClient? agent, SecretManager secrets, CancellationToken ct)
    {
        if (agent is null)
        {
            return NotConnected();
        }

        var bridge = KeyView(secrets, tenantId);
        return await RecallEndpoints.RelayAsync(() => agent.PreviewAsync(ct), preview => Results.Ok(new { preview, bridge })).ConfigureAwait(false);
    }

    internal static async Task<IResult> EnableAsync(
        string tenantId, IStorageAgentClient? agent, SecretManager secrets, Func<string?, bool> verifyPassword, StorageEnableRequest? body, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        if (!string.Equals(body.Confirm, EnableConfirmation, StringComparison.Ordinal))
        {
            return BadRequest($"Type {EnableConfirmation} to confirm.");
        }

        if (secrets.GetStorageKeyExportedAt(tenantId) is null && !body.AcceptNoRecoveryKit)
        {
            return BadRequest("Export the recovery kit first, or confirm that losing this Windows profile can lose every message and memory.");
        }

        if (agent is null)
        {
            return NotConnected();
        }

        return await AgentCallAsync(async () =>
        {
            var (key, refusal) = await KeyForAgentAsync(tenantId, agent, secrets, ct).ConfigureAwait(false);
            if (refusal is not null)
            {
                return refusal;
            }

            if (!await agent.ProvideKeyAsync(key!, ct).ConfigureAwait(false))
            {
                return Conflict("The agent refused the storage key: it already uses another key. Restart the agent and try again.");
            }

            var result = await agent.EnableAsync(
                new StorageEncryptionEnableRequestDto { KeyFingerprint = StorageKeyFingerprint.Compute(key!), EraseEvaluationArtifacts = body.EraseEvaluationArtifacts },
                ct).ConfigureAwait(false);
            return result.Accepted ? Results.Json(result, statusCode: 202) : Results.Json(result, statusCode: 400);
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> ExportKitAsync(
        string tenantId, IStorageAgentClient? agent, SecretManager secrets, Func<string?, bool> verifyPassword, RecoveryKitExportRequest? body, TimeProvider time, CancellationToken ct)
    {
        if (body is null)
        {
            return BadRequest("Request body is required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        if (body.Passphrase is not { Length: >= StorageRecoveryKit.MinimumPassphraseLength } passphrase)
        {
            return BadRequest($"The passphrase needs at least {StorageRecoveryKit.MinimumPassphraseLength} characters.");
        }

        return await AgentCallAsync(async () =>
        {
            string? key;
            try
            {
                key = secrets.TryGetTenantStorageKey(tenantId);
            }
            catch (StorageKeyUnavailableException)
            {
                return Conflict("The storage key of this tenant cannot be decrypted on this Windows profile; restore an earlier recovery kit instead.");
            }

            if (key is null)
            {
                if (agent is null)
                {
                    return NotConnected();
                }

                var (created, refusal) = await KeyForAgentAsync(tenantId, agent, secrets, ct).ConfigureAwait(false);
                if (refusal is not null)
                {
                    return refusal;
                }

                key = created!;
                await agent.ProvideKeyAsync(key, ct).ConfigureAwait(false);
            }

            var now = time.GetUtcNow();
            var kit = StorageRecoveryKit.Create(tenantId, key, passphrase, now);
            secrets.RecordStorageKeyExport(tenantId, now);
            var fingerprint = StorageKeyFingerprint.Compute(key);
            return Results.File(kit, "application/json", $"cortex-recovery-kit-{SafeName(tenantId)}-{fingerprint[..8]}.json");
        }).ConfigureAwait(false);
    }

    internal static async Task<IResult> RestoreKitAsync(
        string tenantId, IStorageAgentClient? agent, SecretManager secrets, Func<string?, bool> verifyPassword, RecoveryKitRestoreRequest? body, CancellationToken ct)
    {
        if (body?.Kit is not { Length: > 0 } kitJson)
        {
            return BadRequest("The recovery kit file is required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        RecoveredStorageKey recovered;
        try
        {
            recovered = StorageRecoveryKit.Open(kitJson, body.Passphrase ?? string.Empty);
        }
        catch (StorageRecoveryKitException ex)
        {
            return BadRequest(ex.Message);
        }

        if (!string.Equals(recovered.TenantId, tenantId, StringComparison.Ordinal))
        {
            return BadRequest($"This recovery kit belongs to tenant '{recovered.TenantId}'.");
        }

        string? current;
        try
        {
            current = secrets.TryGetTenantStorageKey(tenantId);
        }
        catch (StorageKeyUnavailableException)
        {
            current = null;
        }

        if (current is not null && string.Equals(StorageKeyFingerprint.Compute(current), recovered.Fingerprint, StringComparison.Ordinal))
        {
            return Results.Ok(new { restored = false, fingerprint = recovered.Fingerprint, message = "This key is already installed." });
        }

        StorageEncryptionStatusDto? status = null;
        if (agent is not null)
        {
            try
            {
                status = await agent.GetStatusAsync(ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // An unreachable agent only removes the extra check below
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = null;
            }
#pragma warning restore CA1031
        }

        var expected = status?.ExpectedKeyFingerprint;
        if (expected is not null && !string.Equals(expected, recovered.Fingerprint, StringComparison.Ordinal))
        {
            return Conflict("This kit holds another key than the one the agent's data is encrypted with.");
        }

        // A working-looking Bridge key is replaced only on evidence (the agent cannot use it for its encrypted data) or by explicit owner decision.
        var agentRejectsCurrent = status is { HasEncryptedData: true } && (status.KeyState == StorageKeyStates.Rejected || status.MessagesState == "failed" || status.MemoryState == "failed");
        if (current is not null && expected is null && !agentRejectsCurrent && !body.ReplaceExistingKey)
        {
            return Conflict("The Bridge already holds a working storage key and the agent does not report another one; it is not replaced. Confirm the replacement explicitly to override.");
        }

        secrets.ReplaceTenantStorageKey(tenantId, recovered.Base64Key);
        var applied = false;
        if (agent is not null)
        {
            try
            {
                applied = await agent.ProvideKeyAsync(recovered.Base64Key, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // The key is installed; the next connect pushes it
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                applied = false;
            }
#pragma warning restore CA1031
        }

        return Results.Ok(new
        {
            restored = true,
            fingerprint = recovered.Fingerprint,
            applied,
            message = applied ? "Key restored and accepted by the agent." : "Key restored. Restart the agent so it picks the key up.",
        });
    }

    /// <summary>The tenant key to give the agent, creating it only when no encrypted data would be orphaned.</summary>
    private static async Task<(string? Key, IResult? Refusal)> KeyForAgentAsync(string tenantId, IStorageAgentClient agent, SecretManager secrets, CancellationToken ct)
    {
        string? key;
        try
        {
            key = secrets.TryGetTenantStorageKey(tenantId);
        }
        catch (StorageKeyUnavailableException)
        {
            return (null, Conflict("The storage key of this tenant cannot be decrypted on this Windows profile; restore the recovery kit."));
        }

        if (key is not null)
        {
            return (key, null);
        }

        var status = await agent.GetStatusAsync(ct).ConfigureAwait(false);
        if (status.HasEncryptedData)
        {
            return (null, Conflict("The agent holds encrypted data but the Bridge has no key for it; restore the recovery kit."));
        }

        if (status.Unavailable)
        {
            return (null, Conflict("The agent could not report its storage status; no key is created. Try again in a moment."));
        }

        return (secrets.GetOrCreateTenantStorageKey(tenantId), null);
    }

    private static object KeyView(SecretManager secrets, string tenantId)
    {
        try
        {
            var key = secrets.TryGetTenantStorageKey(tenantId);
            return new
            {
                keyPresent = key is not null,
                keyUnavailable = false,
                keyFingerprint = key is null ? null : StorageKeyFingerprint.Compute(key),
                recoveryKitExportedAt = secrets.GetStorageKeyExportedAt(tenantId),
            };
        }
        catch (StorageKeyUnavailableException)
        {
            return new { keyPresent = true, keyUnavailable = true, keyFingerprint = (string?)null, recoveryKitExportedAt = secrets.GetStorageKeyExportedAt(tenantId) };
        }
    }

    private static async Task<IResult> AgentCallAsync(Func<Task<IResult>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HubException ex)
        {
            return BadRequest(ex.Message);
        }
#pragma warning disable CA1031 // Any transport failure is reported as "agent not available", like the other tenant endpoints
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            return Results.Json(new { error = "Agent not available: " + ex.Message }, statusCode: 503);
        }
#pragma warning restore CA1031
    }

    private static string SafeName(string tenantId) => new(tenantId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult Forbidden() => Results.Json(new { error = "The password is not correct." }, statusCode: 403);

    private static IResult Conflict(string error) => Results.Json(new { error }, statusCode: 409);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}
