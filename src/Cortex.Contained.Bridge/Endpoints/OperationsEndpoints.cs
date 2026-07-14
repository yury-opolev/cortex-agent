using Cortex.Contained.Bridge.Hub;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Bridge.Tenants;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Mvc;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>
/// Maps generic, content-free operational-observability endpoints:
/// <c>GET /api/tenants/{tenantId}/operations/subagents</c> (live subagent worker-pool snapshot)
/// and <c>GET /api/operations/mcp-actions</c> (MCP approval-gated action history). Both require
/// authorization. Neither ever surfaces prompt/message/result/argument/eval content — see
/// <see cref="SubagentWorkerSnapshot"/> and <see cref="ProjectAction"/>'s closed field list.
/// The subagent endpoint needs a LIVE agent connection (503 when disconnected); the MCP action
/// history endpoint is Bridge-local (backed by <see cref="IMcpActionStore"/>) and stays
/// available while the agent is disconnected — this is what a future IcM dashboard, or the
/// agent's own workspace files, consume.
/// </summary>
internal static class OperationsEndpoints
{
    private const int DefaultSubagentLimit = 100;
    private const int MinSubagentLimit = 1;
    private const int MaxSubagentLimit = 1000;
    private const int DefaultStaleAfterSeconds = 600;
    private const int MinStaleAfterSeconds = 1;

    private const int DefaultActionLimit = 100;

    /// <summary>Maps the <c>/api/tenants/{tenantId}/operations/subagents</c> and <c>/api/operations/mcp-actions</c> endpoints onto <paramref name="app"/>.</summary>
    public static void MapOperationsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tenants/{tenantId}/operations/subagents", (
            string tenantId,
            [FromServices] TenantRouter router,
            int? limit,
            bool? includeTerminal,
            int? staleAfterSeconds,
            CancellationToken cancellationToken) =>
        {
            var client = router.GetClient(tenantId);
            var query = BuildSubagentQuery(limit, includeTerminal, staleAfterSeconds);
            return GetSubagentSnapshotsAsync(client, query, cancellationToken);
        }).RequireAuthorization();

        app.MapGet("/api/operations/mcp-actions", (
            [FromServices] IMcpActionStore store,
            [FromServices] TenantRegistry tenants,
            string? tenantId,
            string? beforeId,
            int? limit,
            string? serverKey,
            string? toolName,
            string? outcome,
            string? workerTaskId,
            CancellationToken cancellationToken) =>
        {
            var tenant = ResolveTenant(tenants, tenantId);
            if (tenant is null)
            {
                return Task.FromResult(Results.Json(new { error = "No tenant available." }, statusCode: 400));
            }

            return ListActionsAsync(store, tenant, beforeId, limit, serverKey, toolName, outcome, workerTaskId, cancellationToken);
        }).RequireAuthorization();
    }

    // ── Subagents (live, requires a connected agent) ────────────────────────

    /// <summary>
    /// Clamps caller-supplied paging/staleness parameters to the endpoint's documented bounds.
    /// Pure and side-effect-free so the clamping contract is directly testable.
    /// </summary>
    internal static SubagentSnapshotQuery BuildSubagentQuery(int? limit, bool? includeTerminal, int? staleAfterSeconds)
        => new()
        {
            Limit = Math.Clamp(limit ?? DefaultSubagentLimit, MinSubagentLimit, MaxSubagentLimit),
            IncludeTerminal = includeTerminal ?? true,
            StaleAfterSeconds = Math.Max(staleAfterSeconds ?? DefaultStaleAfterSeconds, MinStaleAfterSeconds),
        };

    /// <summary>503 when the agent is not connected; otherwise relays the live snapshot.</summary>
    internal static Task<IResult> GetSubagentSnapshotsAsync(
        HubClient? client, SubagentSnapshotQuery query, CancellationToken cancellationToken)
    {
        if (client is null || !client.IsConnected)
        {
            return Task.FromResult(Results.Json(new { error = "Agent not connected" }, statusCode: 503));
        }

        return CallAgentAsync(client, query, cancellationToken);
    }

    private static async Task<IResult> CallAgentAsync(
        HubClient client, SubagentSnapshotQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await client.GetSubagentSnapshotsAsync(query, cancellationToken).ConfigureAwait(false);
            return Results.Ok(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 503);
        }
    }

    // ── MCP action history (Bridge-local, available while the agent is disconnected) ────

    internal static async Task<IResult> ListActionsAsync(
        IMcpActionStore store,
        string tenantId,
        string? beforeId,
        int? limit,
        string? serverKey,
        string? toolName,
        string? outcome,
        string? workerTaskId,
        CancellationToken cancellationToken)
    {
        McpActionState? outcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(outcome))
        {
            if (!TryParseOutcome(outcome, out var parsed))
            {
                return Results.Json(new { error = $"Unknown outcome '{outcome}'." }, statusCode: 400);
            }

            outcomeFilter = parsed;
        }

        var actions = await store.ListAsync(
            new McpActionQuery
            {
                TenantId = tenantId,
                BeforeActionId = NullIfBlank(beforeId),
                Limit = limit is > 0 ? limit.Value : DefaultActionLimit,
                ServerKey = NullIfBlank(serverKey),
                ToolName = NullIfBlank(toolName),
                State = outcomeFilter,
                WorkerId = NullIfBlank(workerTaskId),
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new { actions = actions.Select(ProjectAction).ToList() }, statusCode: 200);
    }

    /// <summary>
    /// Content-free projection of one MCP action for generic observability: identifiers, hash,
    /// state/outcome, timestamps, duration, server/tool, correlation, and worker id. Deliberately
    /// OMITS <c>CanonicalArgumentsJson</c>, <c>ResultContent</c>, and <c>Error</c> — exact
    /// arguments (and the dispatch result) are available only from the authenticated approval
    /// endpoint (<c>GET /api/mcp/actions/{id}</c>).
    /// </summary>
    internal static object ProjectAction(McpAction action) => new
    {
        actionId = action.ActionId,
        tenantId = action.TenantId,
        invocationId = action.InvocationId,
        argumentsHash = action.ArgumentsHash,
        outcome = McpActionWireStatus.From(action.State),
        serverKey = action.ServerKey,
        toolName = action.ToolName,
        correlationId = action.CorrelationId,
        conversationId = action.ConversationId,
        channelId = action.ChannelId,
        workerId = action.WorkerId,
        createdAtUtc = action.CreatedAtUtc,
        updatedAtUtc = action.UpdatedAtUtc,
        completedAtUtc = action.CompletedAtUtc,
        durationMs = action.CompletedAtUtc.HasValue
            ? (long?)(action.CompletedAtUtc.Value - action.CreatedAtUtc).TotalMilliseconds
            : null,
    };

    private static bool TryParseOutcome(string outcome, out McpActionState parsed)
    {
        foreach (var candidate in Enum.GetValues<McpActionState>())
        {
            if (string.Equals(McpActionWireStatus.From(candidate), outcome, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.ToString(), outcome, StringComparison.OrdinalIgnoreCase))
            {
                parsed = candidate;
                return true;
            }
        }

        parsed = default;
        return false;
    }

    private static string? ResolveTenant(TenantRegistry tenants, string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? tenants.GetDefaultTenant()?.Id : tenantId;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
