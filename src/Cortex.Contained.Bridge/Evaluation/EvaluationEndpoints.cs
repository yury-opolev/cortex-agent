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

/// <summary>
/// Evaluation API (all require authorization): <c>/api/tenants/{tenantId}/eval/jobs</c> (POST start — password
/// again, rate limited; GET list), <c>/eval/jobs/{jobId}</c> (GET), <c>/eval/jobs/{jobId}/cancel</c> (POST),
/// <c>/eval/files/read</c> and <c>/eval/files/write</c> (POST, password again, rate limited: datasets carry
/// personal text). The commands run inside the agent process, which holds the storage key; the Bridge only relays.
/// </summary>
internal static class EvaluationEndpoints
{
    /// <summary>Longest workspace file accepted (datasets are JSONL; 8 Mi characters is far above 500 labelled queries).</summary>
    internal const int MaxFileChars = 8 * 1024 * 1024;

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
