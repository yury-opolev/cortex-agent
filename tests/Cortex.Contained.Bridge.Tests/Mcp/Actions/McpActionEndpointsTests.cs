using System.Security.Cryptography;
using Cortex.Contained.Bridge.Endpoints;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Mcp.Actions;

public sealed class McpActionEndpointsTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";

    private static readonly DateTimeOffset StartTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly IMcpInvocationTarget _target;
    private readonly McpActionDispatchRegistry _registry = new();
    private readonly McpConfigStore _configStore;
    private SqliteMcpActionStore _store = null!;
    private McpActionService _service = null!;

    public McpActionEndpointsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-action-endpoints-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _timeProvider = new FakeTimeProvider(StartTime);
        _target = Substitute.For<IMcpInvocationTarget>();
        var bridgeConfig = new BridgeConfig
        {
            Mcp = new McpSettingsConfig
            {
                Enabled = true,
                Servers =
                [
                    new McpServerConfig
                    {
                        Key = "github",
                        Enabled = true,
                        MutationToolAllowList = ["create_issue"],
                    },
                ],
            },
        };
        _configStore = new McpConfigStore(
            bridgeConfig, Path.Combine(_tempDir, "cortex.yml"), NullLogger<McpConfigStore>.Instance);
    }

    public Task InitializeAsync()
    {
        _store = CreateStore(_tempDir, _timeProvider);
        _service = new McpActionService(
            _store, _target, _configStore, _registry, _timeProvider, NullLogger<McpActionService>.Instance);
        return Task.CompletedTask;
    }

    private static SqliteMcpActionStore CreateStore(string tempDir, FakeTimeProvider timeProvider)
        => new(
            Path.Combine(tempDir, "actions.db"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            timeProvider,
            NullLogger<SqliteMcpActionStore>.Instance);

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    private static int StatusOf(IResult result)
    {
        var statusCodeResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.NotNull(statusCodeResult.StatusCode);
        return statusCodeResult.StatusCode!.Value;
    }

    /// <summary>Proposes one mutation action and returns (actionId, argumentsHash).</summary>
    private async Task<(string ActionId, string ArgumentsHash)> ProposeAsync()
    {
        var result = await _service.InvokeAsync(
            Tenant,
            new McpToolInvocation
            {
                InvocationId = Guid.CreateVersion7().ToString("N"),
                ServerKey = "github",
                ToolName = "create_issue",
                ArgumentsJson = """{"title":"t","body":"b"}""",
            },
            CancellationToken.None);
        return (result.ActionId!, result.ArgumentsHash!);
    }

    // ── Authorization metadata ────────────────────────────────────────────

    [Fact]
    public void AllActionRoutes_RequireAuthorization()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapMcpActionEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/mcp/actions", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(6, endpoints.Count);
        Assert.All(endpoints, endpoint =>
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAuthorizeData>()));
    }

    // ── Get / list ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_UnknownAction_Returns404()
    {
        var result = await McpActionEndpoints.GetAsync(_store, Tenant, "missing", CancellationToken.None);

        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public async Task Get_ExistingAction_Returns200()
    {
        var (actionId, _) = await ProposeAsync();

        var result = await McpActionEndpoints.GetAsync(_store, Tenant, actionId, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
    }

    [Fact]
    public async Task List_UnknownStateFilter_Returns400()
    {
        var result = await McpActionEndpoints.ListAsync(
            _store, Tenant, null, null, "definitely-not-a-state", null, null, null, CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task List_ByState_Returns200()
    {
        await ProposeAsync();

        var result = await McpActionEndpoints.ListAsync(
            _store, Tenant, null, null, "proposed", null, null, null, CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
    }

    // ── Approve ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_MissingHash_Returns400()
    {
        var (actionId, _) = await ProposeAsync();

        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest("", null, null), CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Approve_UnknownAction_Returns404()
    {
        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, "missing",
            new McpActionDecisionRequest("sha256:" + new string('0', 64), null, null), CancellationToken.None);

        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public async Task Approve_StaleHash_Returns409_AndDoesNotMutate()
    {
        var (actionId, _) = await ProposeAsync();

        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest("sha256:" + new string('0', 64), null, null), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Proposed, action!.State);
    }

    [Fact]
    public async Task Approve_ExactHash_Returns200_AndApproves()
    {
        var (actionId, hash) = await ProposeAsync();

        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest(hash, "looks right", null), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, action!.State);
    }

    [Fact]
    public async Task Approve_ExpiredProposal_Returns410()
    {
        var (actionId, hash) = await ProposeAsync();
        _timeProvider.Advance(McpActionService.ProposalTtl + TimeSpan.FromMinutes(1));

        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest(hash, null, null), CancellationToken.None);

        Assert.Equal(410, StatusOf(result));
    }

    [Fact]
    public async Task Approve_PastExpiry_Returns400()
    {
        var (actionId, hash) = await ProposeAsync();

        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest(hash, null, StartTime.AddMinutes(-5)), CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Approve_AlreadyApproved_Returns409()
    {
        var (actionId, hash) = await ProposeAsync();
        await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest(hash, null, null), CancellationToken.None);

        var result = await McpActionEndpoints.ApproveAsync(
            _store, _timeProvider, Tenant, actionId,
            new McpActionDecisionRequest(hash, null, null), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
    }

    // ── Reject ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_ExactHash_Returns200_AndRejects()
    {
        var (actionId, hash) = await ProposeAsync();

        var result = await McpActionEndpoints.RejectAsync(
            _store, Tenant, actionId, new McpActionDecisionRequest(hash, "nope", null), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Rejected, action!.State);
    }

    [Fact]
    public async Task Reject_StaleHash_Returns409()
    {
        var (actionId, _) = await ProposeAsync();

        var result = await McpActionEndpoints.RejectAsync(
            _store, Tenant, actionId,
            new McpActionDecisionRequest("sha256:" + new string('0', 64), null, null), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
    }

    // ── Cancel ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_ExactHash_Returns200_AndCancels()
    {
        var (actionId, hash) = await ProposeAsync();

        var result = await McpActionEndpoints.CancelAsync(
            _service, _store, Tenant, actionId, new McpActionDecisionRequest(hash, null, null), CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Cancelled, action!.State);
    }

    [Fact]
    public async Task Cancel_StaleHash_Returns409()
    {
        var (actionId, _) = await ProposeAsync();

        var result = await McpActionEndpoints.CancelAsync(
            _service, _store, Tenant, actionId,
            new McpActionDecisionRequest("sha256:" + new string('0', 64), null, null), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
    }

    [Fact]
    public async Task Cancel_UnknownAction_Returns404()
    {
        var result = await McpActionEndpoints.CancelAsync(
            _service, _store, Tenant, "missing",
            new McpActionDecisionRequest("sha256:" + new string('0', 64), null, null), CancellationToken.None);

        Assert.Equal(404, StatusOf(result));
    }

    [Fact]
    public async Task Cancel_ExpiredAction_Returns410()
    {
        var (actionId, hash) = await ProposeAsync();
        _timeProvider.Advance(McpActionService.ProposalTtl + TimeSpan.FromMinutes(1));
        await _store.ExpireAsync(_timeProvider.GetUtcNow(), CancellationToken.None);

        var result = await McpActionEndpoints.CancelAsync(
            _service, _store, Tenant, actionId, new McpActionDecisionRequest(hash, null, null), CancellationToken.None);

        Assert.Equal(410, StatusOf(result));
    }

    // ── Reconcile ─────────────────────────────────────────────────────────

    private async Task<(string ActionId, string ArgumentsHash)> DriveToOutcomeUnknownAsync()
    {
        var (actionId, hash) = await ProposeAsync();
        await _store.ApproveAsync(
            Tenant, actionId, hash, "user@local", "ok", _timeProvider.GetUtcNow().AddHours(1), CancellationToken.None);
        var lease = await _store.TryClaimNextApprovedAsync(_timeProvider.GetUtcNow(), CancellationToken.None);
        await _store.CompleteAttemptAsync(
            new McpActionDispatchCompletion
            {
                ActionId = actionId,
                AttemptNumber = lease!.AttemptNumber,
                State = McpActionState.OutcomeUnknown,
                FailureKind = McpFailureKind.Transport,
                Error = "transport lost",
                CompletedAtUtc = _timeProvider.GetUtcNow(),
            },
            CancellationToken.None);
        return (actionId, hash);
    }

    [Fact]
    public async Task Reconcile_MissingEvidence_Returns400()
    {
        var (actionId, hash) = await DriveToOutcomeUnknownAsync();

        var result = await McpActionEndpoints.ReconcileAsync(
            _store, Tenant, actionId,
            new McpActionReconcileRequest(hash, "succeeded", "", null), CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Reconcile_InvalidOutcome_Returns400()
    {
        var (actionId, hash) = await DriveToOutcomeUnknownAsync();

        var result = await McpActionEndpoints.ReconcileAsync(
            _store, Tenant, actionId,
            new McpActionReconcileRequest(hash, "maybe", "checked remote", null), CancellationToken.None);

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Reconcile_WithEvidence_Returns200_AndResolves()
    {
        var (actionId, hash) = await DriveToOutcomeUnknownAsync();

        var result = await McpActionEndpoints.ReconcileAsync(
            _store, Tenant, actionId,
            new McpActionReconcileRequest(hash, "succeeded", "found issue #42 on the remote", "https://github.example/i/42"),
            CancellationToken.None);

        Assert.Equal(200, StatusOf(result));
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.ReconciledSucceeded, action!.State);
        Assert.Equal("https://github.example/i/42", action.RemoteReference);
    }

    [Fact]
    public async Task Reconcile_StaleHash_Returns409_AndDoesNotResolve()
    {
        var (actionId, _) = await DriveToOutcomeUnknownAsync();

        var result = await McpActionEndpoints.ReconcileAsync(
            _store, Tenant, actionId,
            new McpActionReconcileRequest("sha256:" + new string('0', 64), "succeeded", "evidence", null),
            CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, action!.State);
    }

    [Fact]
    public async Task Reconcile_NonUnknownAction_Returns409()
    {
        var (actionId, hash) = await ProposeAsync();

        var result = await McpActionEndpoints.ReconcileAsync(
            _store, Tenant, actionId,
            new McpActionReconcileRequest(hash, "succeeded", "evidence", null), CancellationToken.None);

        Assert.Equal(409, StatusOf(result));
    }
}
