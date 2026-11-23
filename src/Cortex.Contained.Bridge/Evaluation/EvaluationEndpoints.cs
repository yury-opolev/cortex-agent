using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/jobs</c>.</summary>
public sealed record EvaluationJobRequest(string? Password, string? Tool, IReadOnlyList<string>? Args, string? Passphrase = null);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/files/read</c>.</summary>
public sealed record EvaluationFileReadRequest(string? Password, string? Tool, string? Name);

/// <summary>Body of <c>POST /api/tenants/{tenantId}/eval/files/write</c>.</summary>
public sealed record EvaluationFileWriteRequest(string? Password, string? Tool, string? Name, string? Content);

/// <summary>Body of the package download and the off-machine deletion acknowledgement (the Bridge password again).</summary>
public sealed record PasswordBody(string? Password);

/// <summary>
/// Evaluation API (all require authorization): <c>/api/tenants/{tenantId}/eval/jobs</c> (POST start — password
/// again, rate limited; GET list), <c>/eval/jobs/{jobId}</c> (GET), <c>/eval/jobs/{jobId}/cancel</c> (POST),
/// <c>/eval/files/read</c> and <c>/eval/files/write</c> (POST, password again, rate limited: datasets carry
/// personal text), <c>/eval/frozen</c> (GET: versions, packages, pending off-machine deletions),
/// <c>/eval/frozen/packages/{name}</c> (POST, password again, rate limited: downloads a passphrase-encrypted
/// package) and <c>/eval/frozen/{version}/offsite-deleted</c> (POST, password again, rate limited). The commands run inside the
/// agent process, which holds the storage key; the Bridge only relays.
/// </summary>
internal static class EvaluationEndpoints
{
    /// <summary>Longest workspace file accepted (datasets are JSONL; 8 Mi characters is far above 500 labelled queries).</summary>
    internal const int MaxFileChars = 8 * 1024 * 1024;

    /// <summary>Package bytes fetched from the agent per hub call.</summary>
    internal const int PackageChunkBytes = 4 * 1024 * 1024;

    /// <summary>Maps the evaluation endpoints onto <paramref name="app"/>.</summary>
    public static void MapEvaluationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/tenants/{tenantId}/eval/jobs", (string tenantId, EvaluationJobRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
            StartAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapGet("/api/tenants/{tenantId}/eval/jobs", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            ListAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/eval/jobs/{jobId}", (string tenantId, string jobId, [FromServices] TenantRouter router, CancellationToken ct) =>
            GetAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), jobId, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/jobs/{jobId}/cancel", (string tenantId, string jobId, [FromServices] TenantRouter router, CancellationToken ct) =>
            CancelAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), jobId, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/files/read", async (string tenantId, EvaluationFileReadRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ReadFileAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/files/write", (string tenantId, EvaluationFileWriteRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
            WriteFileAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapGet("/api/tenants/{tenantId}/eval/frozen", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            FrozenStatusAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/frozen/packages/{name}", async (string tenantId, string name, PasswordBody body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await DownloadPackageAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), name, body, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/frozen/{version}/offsite-deleted", (string tenantId, string version, PasswordBody body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
            AcknowledgeOffsiteDeletionAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), version, body, ct)).RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
    }

    internal static Task<IResult> FrozenStatusAsync(IEvaluationAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.GetFrozenStatusAsync(ct), Results.Ok);

    /// <summary>
    /// Streams a passphrase-encrypted export package from the agent in chunks (it never exists in plaintext, here or there).
    /// Only <c>*.cxpkg</c> file names the agent lists are served.
    /// </summary>
    internal static async Task<IResult> DownloadPackageAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, string name, PasswordBody? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) || !name.EndsWith(".cxpkg", StringComparison.Ordinal))
        {
            return BadRequest("Not a package name.");
        }

        if (!verifyPassword(body?.Password))
        {
            return Forbidden();
        }

        if (agent is null)
        {
            return NotConnected();
        }

        EvaluationPackageChunkDto first;
        try
        {
            first = await agent.ReadPackageAsync(name, 0, PackageChunkBytes, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any transport failure is reported as "agent not available", like the other tenant endpoints
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = "Agent not available: " + ex.Message }, statusCode: 503);
        }
#pragma warning restore CA1031

        if (first.Error is not null)
        {
            return Results.Json(new { error = first.Error }, statusCode: 404);
        }

        return Results.Stream(
            async stream =>
            {
                var chunk = first;
                long offset = 0;
                while (true)
                {
                    await stream.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                    offset += chunk.Data.Length;
                    if (offset >= chunk.Length || chunk.Data.Length == 0)
                    {
                        break;
                    }

                    chunk = await agent.ReadPackageAsync(name, offset, PackageChunkBytes, ct).ConfigureAwait(false);
                    if (chunk.Error is not null)
                    {
                        throw new InvalidOperationException(chunk.Error);
                    }
                }
            },
            "application/octet-stream",
            name);
    }

    internal static async Task<IResult> AcknowledgeOffsiteDeletionAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, string version, PasswordBody? body, CancellationToken ct)
    {
        if (!verifyPassword(body?.Password))
        {
            return Forbidden();
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(
                () => agent.AcknowledgeOffsiteDeletionAsync(version, ct),
                acknowledged => acknowledged ? Results.Ok(new { acknowledged }) : Results.Json(new { error = $"No off-machine deletion is pending for {version}." }, statusCode: 404)).ConfigureAwait(false);
    }

    internal static async Task<IResult> StartAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationJobRequest? body, CancellationToken ct)
    {
        if (body?.Tool is not { Length: > 0 } tool || body.Args is not { Count: > 0 } args)
        {
            return BadRequest("tool and args are required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        if (agent is null)
        {
            return NotConnected();
        }

        return await RecallEndpoints.RelayAsync(
            () => agent.StartAsync(new EvaluationStartRequestDto { Tool = tool, Args = args, Passphrase = body.Passphrase }, ct),
            job => job.State == EvaluationJobStates.Refused ? Results.Json(job, statusCode: 409) : Results.Json(job, statusCode: 202)).ConfigureAwait(false);
    }

    internal static Task<IResult> ListAsync(IEvaluationAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.ListAsync(ct), jobs => Results.Ok(new { jobs }));

    internal static Task<IResult> GetAsync(IEvaluationAgentClient? agent, string jobId, CancellationToken ct) =>
        agent is null
            ? Task.FromResult(NotConnected())
            : RecallEndpoints.RelayAsync(() => agent.GetAsync(jobId, ct), job => job is null ? Results.Json(new { error = "Unknown job" }, statusCode: 404) : Results.Ok(job));

    internal static Task<IResult> CancelAsync(IEvaluationAgentClient? agent, string jobId, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.CancelAsync(jobId, ct), cancelled => Results.Ok(new { cancelled }));

    internal static async Task<IResult> ReadFileAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationFileReadRequest? body, CancellationToken ct)
    {
        if (body?.Tool is not { Length: > 0 } tool || body.Name is not { Length: > 0 } name)
        {
            return BadRequest("tool and name are required");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(
                () => agent.ReadFileAsync(new EvaluationFileRequestDto { Tool = tool, Name = name }, ct),
                file => file.Error is null ? Results.Ok(file) : Results.Json(file, statusCode: 400)).ConfigureAwait(false);
    }

    internal static async Task<IResult> WriteFileAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, EvaluationFileWriteRequest? body, CancellationToken ct)
    {
        if (body?.Tool is not { Length: > 0 } tool || body.Name is not { Length: > 0 } name || body.Content is not { } content)
        {
            return BadRequest("tool, name and content are required");
        }

        if (content.Length > MaxFileChars)
        {
            return BadRequest($"The file is larger than {MaxFileChars} characters.");
        }

        if (!verifyPassword(body.Password))
        {
            return Forbidden();
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(
                () => agent.WriteFileAsync(new EvaluationFileWriteDto { Tool = tool, Name = name, Content = content }, ct),
                result => result.Error is null ? Results.Ok(result) : Results.Json(result, statusCode: 400)).ConfigureAwait(false);
    }

    // Failed re-authentications are counted per session; the third invalidates it (see SessionManager.VerifyPassword).
    private static Func<string?, bool> Reauthenticate(SessionManager sessions, HttpContext http) =>
        password => sessions.VerifyPassword(password, http.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var token) ? token : null);

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult Forbidden() => Results.Json(new { error = "The password is not correct." }, statusCode: 403);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}
