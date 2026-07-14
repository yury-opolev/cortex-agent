using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging;
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
    public async Task TryBuildMcpActionAggregateAsync_StoreThrows_LogDoesNotContainTheRawExceptionMessage()
    {
        // SECURITY: a store fault message could echo a fragment of a query parameter or
        // connection detail — the /health probe's warning log must carry only the exception TYPE,
        // consistent with the rest of the Bridge-side MCP redaction guarantee (docs/security.md).
        var capturingLogger = new CapturingLogger<HealthEndpointsTests>();
        var store = Substitute.For<IMcpActionStore>();
        var secretLookingMessage = "store unavailable at https://user:s3cr3t@internal.example/db";
        store.ListAsync(Arg.Any<McpActionQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<McpAction>>(new InvalidOperationException(secretLookingMessage)));

        var result = await HealthEndpoints.TryBuildMcpActionAggregateAsync(
            store, Tenant, capturingLogger, CancellationToken.None);

        Assert.Null(result);
        var failureLogs = capturingLogger.Messages
            .Where(m => m.Contains("aggregate probe failed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var failureLog = Assert.Single(failureLogs);
        Assert.Contains(nameof(InvalidOperationException), failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain(secretLookingMessage, failureLog, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", failureLog, StringComparison.Ordinal);
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

    /// <summary>Captures fully-formatted log messages so redaction assertions can inspect them.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => this.Messages.Add(formatter(state, exception));
    }
}
