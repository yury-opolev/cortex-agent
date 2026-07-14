using System.Security.Cryptography;
using Cortex.Contained.Bridge.Mcp;
using Cortex.Contained.Bridge.Mcp.Actions;
using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Cortex.Contained.Bridge.Tests.Mcp.Actions;

public sealed class McpActionDispatcherTests : IAsyncLifetime
{
    private const string Tenant = "tenant-1";

    private static readonly DateTimeOffset StartTime = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir;
    private readonly FakeTimeProvider _timeProvider;
    private readonly IMcpInvocationTarget _target;
    private readonly McpActionDispatchRegistry _registry = new();
    private readonly McpServerConfig _serverConfig;
    private readonly McpSettingsConfig _settings;
    private readonly McpConfigStore _configStore;
    private SqliteMcpActionStore _store = null!;
    private McpActionService _service = null!;
    private McpActionDispatcher _dispatcher = null!;

    public McpActionDispatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-action-dispatcher-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _timeProvider = new FakeTimeProvider(StartTime);
        _target = Substitute.For<IMcpInvocationTarget>();
        _serverConfig = new McpServerConfig
        {
            Key = "github",
            Enabled = true,
            MutationToolAllowList = ["create_issue"],
        };
        _settings = new McpSettingsConfig { Enabled = true, Servers = [_serverConfig] };
        var bridgeConfig = new BridgeConfig { Mcp = _settings };
        _configStore = new McpConfigStore(
            bridgeConfig, Path.Combine(_tempDir, "cortex.yml"), NullLogger<McpConfigStore>.Instance);
    }

    public Task InitializeAsync()
    {
        _store = CreateStore(_tempDir, _timeProvider);
        _service = new McpActionService(
            _store, _target, _configStore, _registry, _timeProvider, NullLogger<McpActionService>.Instance);
        _dispatcher = CreateDispatcher();
        return Task.CompletedTask;
    }

    private static SqliteMcpActionStore CreateStore(string tempDir, FakeTimeProvider timeProvider)
        => new(
            Path.Combine(tempDir, "actions.db"),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            timeProvider,
            NullLogger<SqliteMcpActionStore>.Instance);

    private McpActionDispatcher CreateDispatcher()
        => new(_store, _target, _configStore, _registry, _timeProvider, NullLogger<McpActionDispatcher>.Instance);

    public async Task DisposeAsync()
    {
        _dispatcher.Dispose();
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    /// <summary>Proposes a mutation through the service and approves it, returning (actionId, argumentsHash, invocationId).</summary>
    private async Task<(string ActionId, string ArgumentsHash, string InvocationId)> ProposeAndApproveAsync(
        string argumentsJson = """{"title":"t","body":"b"}""")
    {
        var invocationId = Guid.CreateVersion7().ToString("N");
        var result = await _service.InvokeAsync(
            Tenant,
            new McpToolInvocation
            {
                InvocationId = invocationId,
                ServerKey = "github",
                ToolName = "create_issue",
                ArgumentsJson = argumentsJson,
            },
            CancellationToken.None);
        Assert.Equal(McpToolDisposition.AwaitingApproval, result.Disposition);

        var approved = await _store.ApproveAsync(
            Tenant, result.ActionId!, result.ArgumentsHash!, "user@local", "ok",
            _timeProvider.GetUtcNow().AddHours(6), CancellationToken.None);
        Assert.True(approved.Succeeded);
        return (result.ActionId!, result.ArgumentsHash!, invocationId);
    }

    private void TargetReturns(Func<McpToolInvocation, McpToolResult> factory)
        => _target.InvokeApprovedAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => factory(callInfo.Arg<McpToolInvocation>()));

    [Fact]
    public async Task Dispatcher_UsesStoredCanonicalArguments()
    {
        // Propose with messy, unsorted, whitespace-laden arguments…
        var (actionId, _, invocationId) = await ProposeAndApproveAsync(
            """{ "title": "t", "nested": { "b": 2, "a": 1 }, "body": "b" }""");
        McpToolInvocation? dispatched = null;
        _target.InvokeApprovedAsync(Arg.Do<McpToolInvocation>(i => dispatched = i), Arg.Any<CancellationToken>())
            .Returns(callInfo => McpToolResult.Ok(callInfo.Arg<McpToolInvocation>().InvocationId, "done"));

        var processed = await _dispatcher.ProcessNextAsync(CancellationToken.None);

        // …and the dispatcher sends EXACTLY the stored canonical form under the ORIGINAL
        // invocation id — never the raw arguments the agent supplied.
        Assert.True(processed);
        Assert.NotNull(dispatched);
        Assert.Equal("""{"body":"b","nested":{"a":1,"b":2},"title":"t"}""", dispatched.ArgumentsJson);
        Assert.Equal(invocationId, dispatched.InvocationId);
        Assert.Equal("github", dispatched.ServerKey);
        Assert.Equal("create_issue", dispatched.ToolName);
        await _target.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Succeeded, action!.State);
    }

    [Fact]
    public async Task Dispatcher_Success_MarksSucceeded()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        TargetReturns(invocation => McpToolResult.Ok(invocation.InvocationId, "created #42"));

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Succeeded, action!.State);
        Assert.Equal("created #42", action.ResultContent);
    }

    [Fact]
    public async Task Dispatcher_McpError_MarksFailed()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        TargetReturns(invocation => McpToolResult.Fail(invocation.InvocationId, McpFailureKind.Tool, "boom"));

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Failed, action!.State);
        Assert.Equal("boom", action.Error);
    }

    [Fact]
    public async Task Dispatcher_TransportLoss_MarksOutcomeUnknown()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        TargetReturns(invocation => McpToolResult.Unknown(
            invocation.InvocationId, McpFailureKind.Transport, "transport lost mid-call"));

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, action!.State);
    }

    [Fact]
    public async Task Dispatcher_UnknownOutcome_IsNeverRetried()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        TargetReturns(invocation => McpToolResult.Unknown(
            invocation.InvocationId, McpFailureKind.Timeout, "timed out"));
        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        // CRITICAL INVARIANT: an outcome_unknown action is terminal for the outbox. No amount
        // of subsequent processing — or elapsed time — ever re-dispatches it.
        Assert.False(await _dispatcher.ProcessNextAsync(CancellationToken.None));
        _timeProvider.Advance(TimeSpan.FromHours(2));
        Assert.False(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        await _target.ReceivedWithAnyArgs(1).InvokeApprovedAsync(default!, default);
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, action!.State);
    }

    [Fact]
    public async Task Dispatcher_ServerUnavailableBeforeDispatch_Defers()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        TargetReturns(invocation => McpToolResult.Fail(
            invocation.InvocationId, McpFailureKind.Unavailable, "MCP server 'github' is not available."));

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        // Positively not started → released back to approved with a bounded backoff…
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, action!.State);
        Assert.NotNull(action.NextAttemptAtUtc);
        Assert.True(action.NextAttemptAtUtc > _timeProvider.GetUtcNow());

        // …not claimable again until the backoff elapses…
        Assert.False(await _dispatcher.ProcessNextAsync(CancellationToken.None));
        await _target.ReceivedWithAnyArgs(1).InvokeApprovedAsync(default!, default);

        // …then dispatched again once it does.
        _timeProvider.Advance(TimeSpan.FromMinutes(2));
        TargetReturns(invocation => McpToolResult.Ok(invocation.InvocationId, "done"));
        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));
        var final = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Succeeded, final!.State);
    }

    [Fact]
    public async Task Dispatcher_CancelDuringCall_MarksOutcomeUnknown()
    {
        var (actionId, argumentsHash, _) = await ProposeAndApproveAsync();
        var invokeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invokeResult = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _target.InvokeApprovedAsync(Arg.Any<McpToolInvocation>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var invocation = callInfo.Arg<McpToolInvocation>();
                var token = callInfo.Arg<CancellationToken>();
                // Mirror the real connection behavior: an in-flight cancellation after dispatch
                // began resolves to OutcomeUnknown (the mutation may have executed).
                token.Register(() => invokeResult.TrySetResult(McpToolResult.Unknown(
                    invocation.InvocationId, McpFailureKind.Cancellation, "cancelled mid-call")));
                invokeStarted.TrySetResult();
                return invokeResult.Task;
            });

        var processing = _dispatcher.ProcessNextAsync(CancellationToken.None);
        await invokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Cancel WHILE dispatching: recorded + the active invocation is signalled…
        var cancel = await _service.CancelAsync(Tenant, actionId, argumentsHash, "tester", CancellationToken.None);
        Assert.True(cancel.Accepted);
        Assert.True(await processing.WaitAsync(TimeSpan.FromSeconds(10)));

        // …and the action resolves to outcome_unknown — NEVER cancelled after dispatch began.
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, action!.State);
    }

    [Fact]
    public async Task Dispatcher_RestartMidDispatch_RecoversToOutcomeUnknown_AndNeverRedispatches()
    {
        // Simulate a crash mid-dispatch: the action is claimed (dispatching) and the process
        // dies before any completion is recorded.
        var (actionId, _, _) = await ProposeAndApproveAsync();
        var lease = await _store.TryClaimNextApprovedAsync(_timeProvider.GetUtcNow(), CancellationToken.None);
        Assert.NotNull(lease);

        // "Restart": a fresh dispatcher runs its startup recovery.
        var recovered = await _dispatcher.RecoverAsync(CancellationToken.None);

        Assert.Equal(1, recovered);
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.OutcomeUnknown, action!.State);

        // Never silently succeeded, never re-dispatched.
        Assert.False(await _dispatcher.ProcessNextAsync(CancellationToken.None));
        await _target.DidNotReceiveWithAnyArgs().InvokeApprovedAsync(default!, default);
    }

    [Fact]
    public async Task Dispatcher_ServerDisabledAtDispatchTime_DefersWithoutRemoteCall()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        _serverConfig.Enabled = false;

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Approved, action!.State);
        Assert.NotNull(action.NextAttemptAtUtc);
        await _target.DidNotReceiveWithAnyArgs().InvokeApprovedAsync(default!, default);
    }

    [Fact]
    public async Task Dispatcher_ToolNoLongerMutationClassified_FailsPolicy_WithoutRemoteCall()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        _serverConfig.MutationToolAllowList.Clear();

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        // Policy is re-evaluated at dispatch time; a reclassified tool is refused definitively.
        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Failed, action!.State);
        Assert.Contains("no longer classified", action.Error, StringComparison.OrdinalIgnoreCase);
        await _target.DidNotReceiveWithAnyArgs().InvokeApprovedAsync(default!, default);
    }

    [Fact]
    public async Task Dispatcher_ToolExcludedByAllowList_FailsPolicy_WithoutRemoteCall()
    {
        var (actionId, _, _) = await ProposeAndApproveAsync();
        _serverConfig.ToolAllowList = ["list_issues"];

        Assert.True(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        var action = await _store.GetAsync(Tenant, actionId, CancellationToken.None);
        Assert.Equal(McpActionState.Failed, action!.State);
        await _target.DidNotReceiveWithAnyArgs().InvokeApprovedAsync(default!, default);
    }

    [Fact]
    public async Task Dispatcher_ExpiresStaleProposals_BeforeClaiming()
    {
        var invocationId = Guid.CreateVersion7().ToString("N");
        var proposal = await _service.InvokeAsync(
            Tenant,
            new McpToolInvocation
            {
                InvocationId = invocationId,
                ServerKey = "github",
                ToolName = "create_issue",
                ArgumentsJson = """{"title":"t"}""",
            },
            CancellationToken.None);

        _timeProvider.Advance(McpActionService.ProposalTtl + TimeSpan.FromMinutes(1));
        Assert.False(await _dispatcher.ProcessNextAsync(CancellationToken.None));

        var action = await _store.GetAsync(Tenant, proposal.ActionId!, CancellationToken.None);
        Assert.Equal(McpActionState.Expired, action!.State);
    }
}
