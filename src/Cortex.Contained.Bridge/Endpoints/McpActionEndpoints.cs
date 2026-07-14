using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Bridge.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps the MCP action approval endpoints (<c>/api/mcp/actions*</c>): list/inspect pending
/// mutations and decide them (approve/reject/cancel/reconcile). Every decision is bound to the
/// action's EXACT canonical-argument hash — a stale hash is a 409 and never mutates anything.
/// Every endpoint requires authorization. HTTP semantics: 400 malformed input, 404 absent
/// action, 409 stale hash or invalid state transition, 410 expired.
/// </summary>
internal static class McpActionEndpoints
{
    /// <summary>Default approval validity when the approve request omits <c>expiresAtUtc</c>.</summary>
    internal static readonly TimeSpan DefaultApprovalTtl = TimeSpan.FromHours(1);

    /// <summary>Actor recorded in the audit trail for decisions made through the web API.</summary>
    private const string WebActor = "web-ui";

    /// <summary>Maps the <c>/api/mcp/actions*</c> endpoints onto <paramref name="app"/>.</summary>
    public static void MapMcpActionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/mcp/actions", (
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            string? tenantId,
            string? serverKey,
            string? toolName,
            string? state,
            string? workerId,
            string? before,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var tenant = ResolveTenant(tenants, tenantId);
            if (tenant is null)
            {
                return Task.FromResult(Results.Json(new { error = "No tenant available." }, statusCode: 400));
            }

            return ListAsync(store, tenant, serverKey, toolName, state, workerId, before, limit, cancellationToken);
        }).RequireAuthorization();

        app.MapGet("/api/mcp/actions/{actionId}", (
            string actionId,
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            string? tenantId,
            CancellationToken cancellationToken) =>
            WithTenant(tenants, tenantId, tenant => GetAsync(store, tenant, actionId, cancellationToken))).RequireAuthorization();

        app.MapPost("/api/mcp/actions/{actionId}/approve", (
            string actionId,
            [FromBody] McpActionDecisionRequest? request,
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            [FromServices] TimeProvider timeProvider,
            string? tenantId,
            CancellationToken cancellationToken) =>
            WithTenant(tenants, tenantId, tenant => ApproveAsync(store, timeProvider, tenant, actionId, request, cancellationToken))).RequireAuthorization();

        app.MapPost("/api/mcp/actions/{actionId}/reject", (
            string actionId,
            [FromBody] McpActionDecisionRequest? request,
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            string? tenantId,
            CancellationToken cancellationToken) =>
            WithTenant(tenants, tenantId, tenant => RejectAsync(store, tenant, actionId, request, cancellationToken))).RequireAuthorization();

        app.MapPost("/api/mcp/actions/{actionId}/cancel", (
            string actionId,
            [FromBody] McpActionDecisionRequest? request,
            [FromServices] McpActionService actionService,
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            string? tenantId,
            CancellationToken cancellationToken) =>
            WithTenant(tenants, tenantId, tenant => CancelAsync(actionService, store, tenant, actionId, request, cancellationToken))).RequireAuthorization();

        app.MapPost("/api/mcp/actions/{actionId}/reconcile", (
            string actionId,
            [FromBody] McpActionReconcileRequest? request,
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            string? tenantId,
            CancellationToken cancellationToken) =>
            WithTenant(tenants, tenantId, tenant => ReconcileAsync(store, tenant, actionId, request, cancellationToken))).RequireAuthorization();
    }

    internal static async Task<IResult> ListAsync(
        IMcpActionStore store,
        string tenantId,
        string? serverKey,
        string? toolName,
        string? state,
        string? workerId,
        string? beforeActionId,
        int? limit,
        CancellationToken cancellationToken)
    {
        McpActionState? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!TryParseState(state, out var parsed))
            {
                return Results.Json(new { error = $"Unknown state '{state}'." }, statusCode: 400);
            }

            stateFilter = parsed;
        }

        var actions = await store.ListAsync(
            new McpActionQuery
            {
                TenantId = tenantId,
                ServerKey = NullIfBlank(serverKey),
                ToolName = NullIfBlank(toolName),
                State = stateFilter,
                WorkerId = NullIfBlank(workerId),
                BeforeActionId = NullIfBlank(beforeActionId),
                Limit = limit is > 0 ? limit.Value : 100,
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new { actions = actions.Select(Project).ToList() }, statusCode: 200);
    }

    internal static async Task<IResult> GetAsync(
        IMcpActionStore store, string tenantId, string actionId, CancellationToken cancellationToken)
    {
        var action = await store.GetAsync(tenantId, actionId, cancellationToken).ConfigureAwait(false);
        if (action is null)
        {
            return Results.Json(new { error = $"No MCP action '{actionId}'." }, statusCode: 404);
        }

        return Results.Json(Project(action), statusCode: 200);
    }

    internal static async Task<IResult> ApproveAsync(
        IMcpActionStore store,
        TimeProvider timeProvider,
        string tenantId,
        string actionId,
        McpActionDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ArgumentsHash))
        {
            return Results.Json(new { error = "argumentsHash is required" }, statusCode: 400);
        }

        var now = timeProvider.GetUtcNow();
        var expiresAtUtc = request.ExpiresAtUtc ?? now + DefaultApprovalTtl;
        if (expiresAtUtc <= now)
        {
            return Results.Json(new { error = "expiresAtUtc must be in the future" }, statusCode: 400);
        }

        var result = await store.ApproveAsync(
            tenantId, actionId, request.ArgumentsHash, WebActor, request.Reason, expiresAtUtc, cancellationToken)
            .ConfigureAwait(false);
        return MapDecision(result, actionId);
    }

    internal static async Task<IResult> RejectAsync(
        IMcpActionStore store,
        string tenantId,
        string actionId,
        McpActionDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ArgumentsHash))
        {
            return Results.Json(new { error = "argumentsHash is required" }, statusCode: 400);
        }

        var result = await store.RejectAsync(
            tenantId, actionId, request.ArgumentsHash, WebActor, request.Reason, cancellationToken)
            .ConfigureAwait(false);
        return MapDecision(result, actionId);
    }

    internal static async Task<IResult> CancelAsync(
        McpActionService actionService,
        IMcpActionStore store,
        string tenantId,
        string actionId,
        McpActionDecisionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ArgumentsHash))
        {
            return Results.Json(new { error = "argumentsHash is required" }, statusCode: 400);
        }

        // Route through the action service so cancelling a DISPATCHING action also signals the
        // active invocation (the store alone only records the request).
        var response = await actionService.CancelAsync(
            tenantId, actionId, request.ArgumentsHash, WebActor, cancellationToken).ConfigureAwait(false);
        if (response.Accepted)
        {
            return Results.Json(new { success = true, status = response.Status }, statusCode: 200);
        }

        return response.Error switch
        {
            "not_found" => Results.Json(new { error = $"No MCP action '{actionId}'." }, statusCode: 404),
            "arguments_hash_mismatch" => Results.Json(new { error = "argumentsHash does not match the stored canonical hash" }, statusCode: 409),
            _ => await MapCancelInvalidStateAsync(store, tenantId, actionId, response, cancellationToken).ConfigureAwait(false),
        };
    }

    internal static async Task<IResult> ReconcileAsync(
        IMcpActionStore store,
        string tenantId,
        string actionId,
        McpActionReconcileRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ArgumentsHash))
        {
            return Results.Json(new { error = "argumentsHash is required" }, statusCode: 400);
        }

        if (string.IsNullOrWhiteSpace(request.Evidence))
        {
            return Results.Json(new { error = "evidence is required" }, statusCode: 400);
        }

        bool succeeded;
        if (string.Equals(request.Outcome, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            succeeded = true;
        }
        else if (string.Equals(request.Outcome, "failed", StringComparison.OrdinalIgnoreCase))
        {
            succeeded = false;
        }
        else
        {
            return Results.Json(new { error = "outcome must be 'succeeded' or 'failed'" }, statusCode: 400);
        }

        var result = await store.ReconcileAsync(
            tenantId, actionId, request.ArgumentsHash, succeeded, WebActor, request.Evidence,
            request.RemoteReference, cancellationToken).ConfigureAwait(false);
        return MapDecision(result, actionId);
    }

    private static async Task<IResult> MapCancelInvalidStateAsync(
        IMcpActionStore store,
        string tenantId,
        string actionId,
        Contracts.Hub.McpActionCancelResponse response,
        CancellationToken cancellationToken)
    {
        var action = await store.GetAsync(tenantId, actionId, cancellationToken).ConfigureAwait(false);
        if (action is { State: McpActionState.Expired })
        {
            return Results.Json(new { error = "the action has expired", status = "expired" }, statusCode: 410);
        }

        return Results.Json(
            new { error = response.Error ?? "invalid state", status = response.Status }, statusCode: 409);
    }

    /// <summary>Maps a store decision result to HTTP: 200 ok, 404 absent, 409 stale hash/invalid transition, 410 expired.</summary>
    private static IResult MapDecision(McpActionDecisionResult result, string actionId)
    {
        if (result.Succeeded)
        {
            return Results.Json(Project(result.Action!), statusCode: 200);
        }

        return result.Error switch
        {
            "not_found" => Results.Json(new { error = $"No MCP action '{actionId}'." }, statusCode: 404),
            "arguments_hash_mismatch" => Results.Json(
                new { error = "argumentsHash does not match the stored canonical hash" }, statusCode: 409),
            "proposal_expired" => Results.Json(new { error = "the proposal has expired" }, statusCode: 410),
            "invalid_state" when result.Action is { State: McpActionState.Expired } => Results.Json(
                new { error = "the action has expired", status = "expired" }, statusCode: 410),
            _ => Results.Json(
                new
                {
                    error = result.Error ?? "invalid state",
                    status = result.Action is { } action ? McpActionWireStatus.From(action.State) : null,
                },
                statusCode: 409),
        };
    }

    /// <summary>Secret-free projection of one action for API responses (canonical args included for review).</summary>
    private static object Project(McpAction action) => new
    {
        actionId = action.ActionId,
        tenantId = action.TenantId,
        invocationId = action.InvocationId,
        serverKey = action.ServerKey,
        toolName = action.ToolName,
        canonicalArgumentsJson = action.CanonicalArgumentsJson,
        argumentsHash = action.ArgumentsHash,
        status = McpActionWireStatus.From(action.State),
        proposalExpiresAtUtc = action.ProposalExpiresAtUtc,
        approvalExpiresAtUtc = action.ApprovalExpiresAtUtc,
        nextAttemptAtUtc = action.NextAttemptAtUtc,
        resultContent = action.ResultContent,
        error = action.Error,
        remoteReference = action.RemoteReference,
        conversationId = action.ConversationId,
        channelId = action.ChannelId,
        workerId = action.WorkerId,
        createdAtUtc = action.CreatedAtUtc,
        updatedAtUtc = action.UpdatedAtUtc,
        completedAtUtc = action.CompletedAtUtc,
    };

    private static async Task<IResult> WithTenant(
        TenantRegistry tenants, string? tenantId, Func<string, Task<IResult>> handler)
    {
        var tenant = ResolveTenant(tenants, tenantId);
        if (tenant is null)
        {
            return Results.Json(new { error = "No tenant available." }, statusCode: 400);
        }

        return await handler(tenant).ConfigureAwait(false);
    }

    private static string? ResolveTenant(TenantRegistry tenants, string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? tenants.GetDefaultTenant()?.Id : tenantId;

    private static bool TryParseState(string state, out McpActionState parsed)
    {
        foreach (var candidate in Enum.GetValues<McpActionState>())
        {
            if (string.Equals(McpActionWireStatus.From(candidate), state, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ToString(), state, StringComparison.OrdinalIgnoreCase))
            {
                parsed = candidate;
                return true;
            }
        }

        parsed = default;
        return false;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
