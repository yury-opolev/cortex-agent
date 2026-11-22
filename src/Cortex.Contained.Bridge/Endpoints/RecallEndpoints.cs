using Cortex.Contained.Bridge.Recall;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// The Recall page API (all require authorization). Per tenant, relayed to that tenant's agent:
/// <c>/api/tenants/{tenantId}/recall/settings</c> (GET, PUT), <c>/status</c>, <c>/purge/preview</c>,
/// <c>/purge</c>, <c>/quarantine</c> (paged), <c>/quarantine/ids</c>, <c>/quarantine/keep</c>, <c>/quarantine/delete</c>. Bridge-wide:
/// <c>/api/recall/reranker</c> (GET, PUT) — the reranker sidecar is shared by every tenant.
/// The handlers are internal static seams so they are tested without a web host.
/// </summary>
internal static class RecallEndpoints
{
    /// <summary>Longest channel id accepted in a purge body.</summary>
    internal const int MaxChannelIdLength = 256;

    /// <summary>Maps the Recall endpoints onto <paramref name="app"/>.</summary>
    public static void MapRecallEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tenants/{tenantId}/recall/settings", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            GetSettingsAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapPut("/api/tenants/{tenantId}/recall/settings", (string tenantId, RecallSettingsPatchDto patch, [FromServices] TenantRouter router, CancellationToken ct) =>
            UpdateSettingsAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), patch, ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/recall/status", (string tenantId, [FromServices] TenantRouter router, [FromServices] RerankerSwitch reranker, CancellationToken ct) =>
            GetStatusAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), reranker.Current, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/purge/preview", (string tenantId, HistoryPurgeRequestDto body, [FromServices] TenantRouter router, CancellationToken ct) =>
            PreviewPurgeAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/purge", (string tenantId, HistoryPurgeRequestDto body, [FromServices] TenantRouter router, CancellationToken ct) =>
            PurgeAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/recall/quarantine", (string tenantId, int? offset, int? limit, [FromServices] TenantRouter router, CancellationToken ct) =>
            ListQuarantineAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), offset, limit, ct)).RequireAuthorization();

        app.MapGet("/api/tenants/{tenantId}/recall/quarantine/ids", (string tenantId, [FromServices] TenantRouter router, CancellationToken ct) =>
            ListQuarantineIdsAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/quarantine/keep", (string tenantId, QuarantineIdsRequest body, [FromServices] TenantRouter router, CancellationToken ct) =>
            DecideAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, keep: true, ct)).RequireAuthorization();

        app.MapPost("/api/tenants/{tenantId}/recall/quarantine/delete", (string tenantId, QuarantineIdsRequest body, [FromServices] TenantRouter router, CancellationToken ct) =>
            DecideAsync(HubRecallAgentClient.For(router.GetClient(tenantId)), body, keep: false, ct)).RequireAuthorization();

        app.MapGet("/api/recall/reranker", ([FromServices] RerankerSwitch reranker) => Results.Ok(reranker.Current)).RequireAuthorization();

        app.MapPut("/api/recall/reranker", (RerankerToggleRequest body, [FromServices] RerankerSwitch reranker) =>
            SetReranker(reranker, body, converge => _ = Task.Run(() => converge(CancellationToken.None), CancellationToken.None))).RequireAuthorization();
    }

    internal static Task<IResult> GetSettingsAsync(IRecallAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RelayAsync(() => agent.GetSettingsAsync(ct), Results.Ok);

    internal static Task<IResult> UpdateSettingsAsync(IRecallAgentClient? agent, RecallSettingsPatchDto? patch, CancellationToken ct)
    {
        if (patch is null)
        {
            return Task.FromResult(BadRequest("Request body is required"));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(
                () => agent.UpdateSettingsAsync(patch, ct),
                result => result.Applied ? Results.Ok(result) : Results.Json(result, statusCode: 400));
    }

    internal static Task<IResult> GetStatusAsync(IRecallAgentClient? agent, RerankerSwitchView reranker, CancellationToken ct) =>
        agent is null
            ? Task.FromResult(Results.Json(new { connected = false, reranker }, statusCode: 503))
            : RelayAsync(() => agent.GetStatusAsync(ct), status => Results.Ok(new { connected = true, agent = status, reranker }));

    internal static Task<IResult> PreviewPurgeAsync(IRecallAgentClient? agent, HistoryPurgeRequestDto? body, CancellationToken ct)
    {
        if (ValidatePurge(body) is { } error)
        {
            return Task.FromResult(BadRequest(error));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(
                () => agent.PreviewPurgeAsync(Normalize(body!), ct),
                preview => Results.Ok(new
                {
                    preview.Messages,
                    preview.DependentMemories,
                    preview.RangeFrom,
                    preview.RangeTo,
                    Confirmations = new
                    {
                        Review = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Review),
                        Delete = HistoryPurgeConfirmation.Describe(preview, DependentMemoryActions.Delete),
                    },
                }));
    }

    internal static Task<IResult> PurgeAsync(IRecallAgentClient? agent, HistoryPurgeRequestDto? body, CancellationToken ct)
    {
        if (ValidatePurge(body) is { } error)
        {
            return Task.FromResult(BadRequest(error));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(() => agent.PurgeAsync(Normalize(body!), ct), result => Results.Ok(HistoryDeletionResponse.From(result)));
    }

    /// <summary>One page (default <see cref="QuarantineLimits.DefaultPageSize"/>) of the quarantined memories plus the total.</summary>
    internal static Task<IResult> ListQuarantineAsync(IRecallAgentClient? agent, int? offset, int? limit, CancellationToken ct)
    {
        var skip = offset ?? 0;
        var take = limit ?? QuarantineLimits.DefaultPageSize;
        if (skip < 0 || take < 1 || take > QuarantineLimits.MaxPageSize)
        {
            return Task.FromResult(BadRequest($"offset must not be negative and limit must be between 1 and {QuarantineLimits.MaxPageSize}"));
        }

        return agent is null ? Task.FromResult(NotConnected()) : RelayAsync(() => agent.ListQuarantineAsync(skip, take, ct), Results.Ok);
    }

    /// <summary>Every quarantined memory id, for "select all".</summary>
    internal static Task<IResult> ListQuarantineIdsAsync(IRecallAgentClient? agent, CancellationToken ct) =>
        agent is null ? Task.FromResult(NotConnected()) : RelayAsync(() => agent.ListQuarantineIdsAsync(ct), ids => Results.Ok(new { ids }));

    internal static Task<IResult> DecideAsync(IRecallAgentClient? agent, QuarantineIdsRequest? body, bool keep, CancellationToken ct)
    {
        if (body?.MemoryIds is not { Count: > 0 } ids)
        {
            return Task.FromResult(BadRequest("memoryIds must list at least one memory"));
        }

        if (ids.Count > QuarantineLimits.MaxBatch)
        {
            return Task.FromResult(BadRequest($"At most {QuarantineLimits.MaxBatch} memories per request"));
        }

        return agent is null
            ? Task.FromResult(NotConnected())
            : RelayAsync(() => keep ? agent.KeepAsync(ids, ct) : agent.DeleteAsync(ids, ct), Results.Ok);
    }

    /// <summary>Persists the switch, starts converging (sidecar + grants) and returns the new view.</summary>
    internal static IResult SetReranker(RerankerSwitch reranker, RerankerToggleRequest? body, Action<Func<CancellationToken, Task>> startConverge)
    {
        ArgumentNullException.ThrowIfNull(reranker);
        ArgumentNullException.ThrowIfNull(startConverge);
        if (body is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            reranker.Set(body.Enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new { error = "Could not save cortex.yml: " + ex.Message }, statusCode: 500);
        }

        startConverge(reranker.ConvergeAsync);
        return Results.Ok(reranker.Current);
    }

    /// <summary>Null when <paramref name="body"/> is a usable purge request; otherwise the error.</summary>
    internal static string? ValidatePurge(HistoryPurgeRequestDto? body)
    {
        if (body is null)
        {
            return "Request body is required";
        }

        if (!DependentMemoryActions.IsValid(body.DependentMemories))
        {
            return "dependentMemories must be review or delete";
        }

        if (body.From is { } from && body.OlderThan is { } olderThan && from >= olderThan)
        {
            return "from must be before olderThan";
        }

        if (body.ExpectedMessages is < 0)
        {
            return "expectedMessages must not be negative";
        }

        if (body.ChannelId is { } channel && (channel.Length > MaxChannelIdLength || channel.Any(char.IsControl)))
        {
            return "channelId is invalid";
        }

        return null;
    }

    /// <summary>Runs an agent call: agent-side rejections become 400, a lost connection 503.</summary>
    internal static async Task<IResult> RelayAsync<T>(Func<Task<T>> call, Func<T, IResult> ok)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(ok);
        T value;
        try
        {
            value = await call().ConfigureAwait(false);
        }
        catch (HubException ex) when (ex.Message.Contains(HistoryPurgeErrors.ScopeChangedPrefix, StringComparison.Ordinal))
        {
            return Results.Json(new { error = "The history to delete changed since the preview. Preview again." }, statusCode: 409);
        }
        catch (HubException ex)
        {
            return BadRequest(ex.Message);
        }
#pragma warning disable CA1031 // Any transport failure is reported as "agent not available", like the other tenant endpoints
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = "Agent not available: " + ex.Message }, statusCode: 503);
        }
#pragma warning restore CA1031

        return ok(value);
    }

    private static HistoryPurgeRequestDto Normalize(HistoryPurgeRequestDto body) => body with
    {
        ChannelId = string.IsNullOrWhiteSpace(body.ChannelId) ? null : body.ChannelId.Trim(),
        DependentMemories = DependentMemoryActions.IsDelete(body.DependentMemories) ? DependentMemoryActions.Delete : DependentMemoryActions.Review,
    };

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult NotConnected() => Results.Json(new { error = "Agent not connected" }, statusCode: 503);
}

/// <summary>Body of the quarantine keep/delete endpoints.</summary>
public sealed record QuarantineIdsRequest(IReadOnlyList<string>? MemoryIds);

/// <summary>Body of <c>PUT /api/recall/reranker</c>.</summary>
public sealed record RerankerToggleRequest(bool Enabled);

/// <summary>Limits shared by the quarantine endpoints and the agent.</summary>
public static class QuarantineLimits
{
    /// <summary>Largest id list per request (the agent enforces the same bound).</summary>
    public const int MaxBatch = 1000;

    /// <summary>Page size of the review list when the caller does not choose one.</summary>
    public const int DefaultPageSize = 200;

    /// <summary>Largest review-list page (the agent enforces the same bound).</summary>
    public const int MaxPageSize = 500;
}
