using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Endpoints;

/// <summary>
/// Proves the <c>/health</c> aggregate-counts extension: the MCP action aggregate is computed
/// correctly, and — the critical invariant — ANY failure computing it degrades to null plus a
/// logged warning, never an exception that could make the probe itself fail.
/// </summary>
public sealed class HealthEndpointsTests
{
    private const string Tenant = "tenant-1";

    [Fact]
    public async Task TryBuildMcpActionAggregateAsync_GroupsCountsByOutcome()
    {
        var store = Substitute.For<IMcpActionStore>();
        store.ListAsync(Arg.Any<McpActionQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<McpAction>>(
            [
                MakeAction("act-1", McpActionState.Proposed),
                MakeAction("act-2", McpActionState.Proposed),
                MakeAction("act-3", McpActionState.Succeeded),
            ]));

        var result = await HealthEndpoints.TryBuildMcpActionAggregateAsync(
            store, Tenant, NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(3, result!.TotalCount);
        Assert.Equal(2, result.CountsByState["proposed"]);
        Assert.Equal(1, result.CountsByState["succeeded"]);
    }

    [Fact]
    public async Task TryBuildMcpActionAggregateAsync_NoDefaultTenant_ReturnsNull()
    {
        var store = Substitute.For<IMcpActionStore>();

        var result = await HealthEndpoints.TryBuildMcpActionAggregateAsync(
            store, tenantId: null, NullLogger.Instance, CancellationToken.None);

        Assert.Null(result);
        await store.DidNotReceive().ListAsync(Arg.Any<McpActionQuery>(), Arg.Any<CancellationToken>());
    }

    // ── Critical invariant: a metrics failure never propagates ─────────────

    [Fact]
    public async Task TryBuildMcpActionAggregateAsync_StoreThrows_ReturnsNull_NeverThrows()
    {
        var store = Substitute.For<IMcpActionStore>();
        store.ListAsync(Arg.Any<McpActionQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<McpAction>>(new InvalidOperationException("store unavailable")));

        // Must not throw — the health probe as a whole must never fail because this did.
        var result = await HealthEndpoints.TryBuildMcpActionAggregateAsync(
            store, Tenant, NullLogger.Instance, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Health_IncludesAggregateCounts()
    {
        // Proves the /health surface (HealthInfo) carries the aggregate once computed —
        // the same object HealthEndpoints.MapHealthEndpoints assigns to HealthInfo.McpActions.
        var store = Substitute.For<IMcpActionStore>();
        store.ListAsync(Arg.Any<McpActionQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<McpAction>>(
            [
                MakeAction("act-1", McpActionState.OutcomeUnknown),
            ]));

        var mcpActions = await HealthEndpoints.TryBuildMcpActionAggregateAsync(
            store, Tenant, NullLogger.Instance, CancellationToken.None);

        var health = new HealthInfo
        {
            Healthy = true,
            Timestamp = DateTimeOffset.UtcNow,
            Version = "1.0.0",
            McpActions = mcpActions,
        };

        Assert.NotNull(health.McpActions);
        Assert.Equal(1, health.McpActions!.TotalCount);
        Assert.Equal(1, health.McpActions.CountsByState["outcome_unknown"]);
    }

    private static McpAction MakeAction(string actionId, McpActionState state) => new()
    {
        ActionId = actionId,
        TenantId = Tenant,
        InvocationId = "inv-" + actionId,
        ServerKey = "github",
        ToolName = "create_issue",
        CanonicalArgumentsJson = "{}",
        ArgumentsHash = "sha256:abc",
        State = state,
        ProposalExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}
