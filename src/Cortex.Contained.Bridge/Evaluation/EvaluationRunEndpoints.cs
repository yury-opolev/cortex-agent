using Cortex.Contained.Bridge.Auth;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Evaluation;

/// <summary>Body of the run commands that need the Bridge password again.</summary>
public sealed record RunCommandRequest(string? Password, bool Fresh = false, IReadOnlyList<DictionaryTermDto>? Terms = null, string? Passphrase = null);

/// <summary>Body of <c>POST …/eval/grading/items/{index}/grade</c>: 0, 1, 2 or null (clear).</summary>
public sealed record GradeRequest(int? Grade);

/// <summary>
/// "Run evaluation" API (all require authorization): <c>/api/tenants/{tenantId}/eval/run</c> (GET),
/// <c>/eval/run/start</c>, <c>/resume</c>, <c>/dictionary</c>, <c>/export</c> (POST, password again, rate limited),
/// <c>/eval/run/cancel</c> (POST), <c>/eval/run/reports/{key}</c> (GET, ids and numbers only),
/// <c>/eval/grading/session</c> (POST, password again: issues a grading grant) and
/// <c>/eval/grading/items/{index}</c> and <c>…/grade</c> (POST with the grant header, rate limited, no-store: the
/// item carries decrypted text). The agent does the work; the Bridge relays.
/// </summary>
internal static class EvaluationRunEndpoints
{
    /// <summary>Maps the endpoints onto <paramref name="app"/>.</summary>
    public static void MapEvaluationRunEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tenants/{tenantId}/eval/run", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            RunAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        foreach (var action in new[] { EvaluationRunActions.Start, EvaluationRunActions.Resume, EvaluationRunActions.Export, "dictionary" })
        {
            var command = action == "dictionary" ? EvaluationRunActions.ApproveDictionary : action;
            app.MapPost($"/api/tenants/{{tenantId}}/eval/run/{action}", (string tenantId, RunCommandRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] SessionManager sessions, CancellationToken ct) =>
                ControlAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Reauthenticate(sessions, http), command, body, ct))
                .RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);
        }

        app.MapPost("/api/tenants/{tenantId}/eval/run/cancel", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            ControlAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), _ => true, EvaluationRunActions.Cancel, new RunCommandRequest(null), ct))
            .RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/eval/run/reports/{key}", (string tenantId, string key, [FromServices] TenantRouter router, CancellationToken ct) =>
            ReportAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), key, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/eval/grading/session", (PasswordBody body, HttpContext http, [FromServices] SessionManager sessions, [FromServices] GradingGrants grants) =>
            Session(Reauthenticate(sessions, http), grants, SessionCookie(http), body))
            .RequireAuthorization().RequireRateLimiting(ReauthRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/grading/items/{index:int}", async (string tenantId, int index, HttpContext http, [FromServices] TenantRouter router, [FromServices] GradingGrants grants, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return await ItemAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Granted(grants, http), index, ct).ConfigureAwait(false);
        }).RequireAuthorization().RequireRateLimiting(GradingRateLimit.PolicyName);

        app.MapPost("/api/tenants/{tenantId}/eval/grading/items/{index:int}/grade", (string tenantId, int index, GradeRequest body, HttpContext http, [FromServices] TenantRouter router, [FromServices] GradingGrants grants, CancellationToken ct) =>
            GradeAsync(HubEvaluationAgentClient.For(router.GetClient(tenantId)), Granted(grants, http), index, body, ct))
            .RequireAuthorization().RequireRateLimiting(GradingRateLimit.PolicyName);
    }

    internal static Task<IResult> RunAsync(IEvaluationAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RecallEndpoints.RelayAsync(() => agent.GetRunAsync(ct), Results.Ok);

    internal static async Task<IResult> ControlAsync(IEvaluationAgentClient? agent, Func<string?, bool> verifyPassword, string action, RunCommandRequest? body, CancellationToken ct)
    {
        if (!verifyPassword(body?.Password))
        {
            return Results.Json(new { error = "The password is not correct." }, statusCode: 403);
        }

        if (agent is null)
        {
            return NotConnected();
        }

        var command = new EvaluationRunCommandDto { Action = action, Fresh = body?.Fresh ?? false, Terms = body?.Terms, Passphrase = body?.Passphrase };
        return await RecallEndpoints.RelayAsync(
            () => agent.ControlRunAsync(command, ct),
            result => result.Ok ? Results.Ok(result) : Results.Json(result, statusCode: 409)).ConfigureAwait(false);
    }

    internal static Task<IResult> ReportAsync(IEvaluationAgentClient? agent, string key, CancellationToken ct) =>
        agent is null
            ? Task.FromResult(NotConnected())
            : RecallEndpoints.RelayAsync(
                () => agent.ReadRunReportAsync(key, ct),
                text => text is null ? Results.Json(new { error = "No such report." }, statusCode: 404) : Results.Text(text, "text/plain; charset=utf-8"));

    internal static IResult Session(Func<string?, bool> verifyPassword, GradingGrants grants, string? session, PasswordBody? body) =>
        verifyPassword(body?.Password)
            ? Results.Ok(new { grant = grants.Issue(session), idleMinutes = GradingGrants.IdleTimeout.TotalMinutes })
            : Results.Json(new { error = "The password is not correct." }, statusCode: 403);

    internal static async Task<IResult> ItemAsync(IEvaluationAgentClient? agent, bool granted, int index, CancellationToken ct)
    {
        if (!granted)
        {
            return GrantExpired();
        }

        return agent is null ? NotConnected() : await RecallEndpoints.RelayAsync(() => agent.GetGradingItemAsync(index, ct), Results.Ok).ConfigureAwait(false);
    }

    internal static async Task<IResult> GradeAsync(IEvaluationAgentClient? agent, bool granted, int index, GradeRequest? body, CancellationToken ct)
    {
        if (!granted)
        {
            return GrantExpired();
        }

        if (body?.Grade is < 0 or > 2)
        {
            return Results.Json(new { error = "Grades are 0, 1 or 2." }, statusCode: 400);
        }

        return agent is null
            ? NotConnected()
            : await RecallEndpoints.RelayAsync(() => agent.SubmitGradeAsync(new GradeRequestDto(index, body?.Grade), ct), Results.Ok).ConfigureAwait(false);
    }

    private static string? SessionCookie(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CortexSessionAuthHandler.CookieName, out var token) ? token : null;

    private static bool Granted(GradingGrants grants, HttpContext http) =>
        grants.Validate(http.Request.Headers[GradingGrants.HeaderName].ToString(), SessionCookie(http));

    // Failed re-authentications are counted per session; the third invalidates it (see SessionManager.VerifyPassword).
    private static Func<string?, bool> Reauthenticate(SessionManager sessions, HttpContext http) =>
        password => sessions.VerifyPassword(password, SessionCookie(http));

    // 403, not 401: a 401 makes the web UI log the owner out.
    private static IResult GrantExpired() => Results.Json(new { error = "The grading session expired: enter the Bridge password again.", grantExpired = true }, statusCode: 403);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}
