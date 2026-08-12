using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Bridge.Tests.Coding.FakeCoda;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Covers the queryable source of truth for a parked prompt. The requestId used to be delivered
/// exactly once (as injected envelope text) and was recorded nowhere, so losing that single
/// message stranded the session: nothing could name what was pending and nothing timed out.
/// These tests pin the three guarantees that close that hole — the parked prompt is readable
/// off the session, an unknown requestId is reported as such rather than silently accepted,
/// and an unanswered prompt is bounded by a timeout.
/// </summary>
public sealed class CodaSessionPendingRequestTests : IAsyncLifetime
{
    // Expiry needs the configured timeout plus one watchdog tick; keep headroom for a loaded box.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly List<IAsyncDisposable> disposables = [];

    private (FakeCodaServer Server, CodaSession Session) NewSession(CodaOptions? options = null)
    {
        var (server, clientStream) = FakeCodaServer.Create();
        var connection = new CodaJsonRpcConnection(clientStream, clientStream);
        var session = new CodaSession(
            "pending-session",
            "ch-pending",
            "C:\\repos\\test",
            CodingPolicy.Prompt,
            connection,
            NullLogger<CodaSession>.Instance,
            options);

        // Session first: disposing it shuts the connection down before the server goes away.
        this.disposables.Add(session);
        this.disposables.Add(server);
        return (server, session);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // xUnit v2 only invokes IAsyncLifetime (not IAsyncDisposable) on a test class, and these
    // sessions each run a 250ms-tick watchdog — leaking them would leave timers running.
    public async Task DisposeAsync()
    {
        foreach (var disposable in this.disposables)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch
            {
                // best-effort teardown
            }
        }
    }

    // -----------------------------------------------------------------------
    // PendingRequest — the parked prompt is readable off the session
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PendingRequest_IsNull_WhenNothingIsParked()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Happy;

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(session.PendingRequest);
    }

    [Fact]
    public async Task PendingRequest_Permission_CarriesRequestIdToolNameAndPreview()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var evt = await permissionSignal.Task.WaitAsync(Timeout);

        var pending = session.PendingRequest;
        Assert.NotNull(pending);
        Assert.Equal(evt.RequestId, pending!.RequestId);
        Assert.Equal(PendingCodingRequestKind.Permission, pending.Kind);
        Assert.Equal("Bash", pending.ToolName);
        Assert.Equal("rm -rf /tmp/x", pending.InputPreview);
    }

    [Fact]
    public async Task PendingRequest_Question_CarriesQuestionAndOptions()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Question;

        var questionSignal = new TaskCompletionSource<CodaQuestionEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Question += evt => questionSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("What approach?", CancellationToken.None).WaitAsync(Timeout);

        var evt = await questionSignal.Task.WaitAsync(Timeout);

        var pending = session.PendingRequest;
        Assert.NotNull(pending);
        Assert.Equal(evt.RequestId, pending!.RequestId);
        Assert.Equal(PendingCodingRequestKind.Question, pending.Kind);
        Assert.Equal("Which approach?", pending.Question);
        Assert.Equal(["A", "B"], pending.Options);
    }

    [Fact]
    public async Task PendingRequest_Plan_CarriesPlanText()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Plan;

        var planSignal = new TaskCompletionSource<CodaPlanApprovalEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PlanApproval += evt => planSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Execute the plan.", CancellationToken.None).WaitAsync(Timeout);

        var evt = await planSignal.Task.WaitAsync(Timeout);

        var pending = session.PendingRequest;
        Assert.NotNull(pending);
        Assert.Equal(evt.RequestId, pending!.RequestId);
        Assert.Equal(PendingCodingRequestKind.Plan, pending.Kind);
        Assert.Contains("Step 1", pending.Plan);
    }

    [Fact]
    public async Task PendingRequest_IsCleared_AfterRespond()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var evt = await permissionSignal.Task.WaitAsync(Timeout);
        await session.RespondAsync(evt.RequestId, "allow_once");

        Assert.Null(session.PendingRequest);
    }

    // -----------------------------------------------------------------------
    // RespondAsync — an unknown requestId must be distinguishable from an answer
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RespondAsync_KnownRequestId_ReportsResolved()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var evt = await permissionSignal.Task.WaitAsync(Timeout);

        Assert.True(await session.RespondAsync(evt.RequestId, "allow_once"));
    }

    [Fact]
    public async Task RespondAsync_UnknownRequestId_ReportsNotResolved()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Happy;

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);

        Assert.False(await session.RespondAsync("no-such-request", "allow_once"));
    }

    // -----------------------------------------------------------------------
    // Expiry — an unanswered prompt must not block coda forever
    // -----------------------------------------------------------------------

    [Fact]
    public async Task PendingPermission_IsAutoDenied_WhenTimeoutElapses()
    {
        var (server, session) = this.NewSession(new CodaOptions { PendingRequestTimeoutSeconds = 1 });
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        var expirySignal = new TaskCompletionSource<CodaPromptExpiredEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PromptExpired += evt => expirySignal.TrySetResult(evt);

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var requested = await permissionSignal.Task.WaitAsync(Timeout);

        // Nobody answers — the watchdog must resolve the prompt so coda's blocked RPC returns.
        await finalResultSignal.Task.WaitAsync(Timeout);

        // The safety-critical half: coda must have been told NO, not yes.
        Assert.False(server.LastPermissionReply!["allow"]!.GetValue<bool>());

        Assert.Null(session.PendingRequest);
        Assert.NotNull(session.LastPromptExpiry);
        Assert.Contains("Bash", session.LastPromptExpiry);

        var expiry = await expirySignal.Task.WaitAsync(Timeout);
        Assert.Equal(requested.RequestId, expiry.RequestId);
        Assert.Equal(PendingCodingRequestKind.Permission, expiry.Kind);
    }

    [Fact]
    public async Task PendingPlan_IsAutoRejected_WhenTimeoutElapses()
    {
        var (server, session) = this.NewSession(new CodaOptions { PendingRequestTimeoutSeconds = 1 });
        server.Scenario = FakeCodaScenario.Plan;

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Execute the plan.", CancellationToken.None).WaitAsync(Timeout);

        await finalResultSignal.Task.WaitAsync(Timeout);

        Assert.False(server.LastPlanReply!["approve"]!.GetValue<bool>());
        Assert.Contains("auto-rejected", session.LastPromptExpiry);
    }

    [Fact]
    public async Task PendingQuestion_IsAnsweredWithAnExplanation_WhenTimeoutElapses()
    {
        var (server, session) = this.NewSession(new CodaOptions { PendingRequestTimeoutSeconds = 1 });
        server.Scenario = FakeCodaScenario.Question;

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("What approach?", CancellationToken.None).WaitAsync(Timeout);

        await finalResultSignal.Task.WaitAsync(Timeout);

        // A question cannot be "refused" — coda gets prose telling it to use its judgement.
        Assert.Contains("No answer arrived", server.LastQuestionReply!["answer"]!.GetValue<string>());
        Assert.Contains("unanswered", session.LastPromptExpiry);
    }

    [Fact]
    public async Task PendingRequest_IsNotExpired_WhenTimeoutIsDisabled()
    {
        // PromptIdleTimeoutSeconds drives the tick (4/4 = 1s), so the watchdog provably runs
        // several times during the wait — proving the disable flag, not merely that time is short.
        var (server, session) = this.NewSession(new CodaOptions
        {
            PendingRequestTimeoutSeconds = 0,
            PromptIdleTimeoutSeconds = 4,
        });
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        await permissionSignal.Task.WaitAsync(Timeout);

        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        Assert.NotNull(session.PendingRequest);
        Assert.Null(session.LastPromptExpiry);
    }

    // -----------------------------------------------------------------------
    // Session death — a prompt must never outlive the session that asked it
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AnsweredPrompt_ReturnsTheSessionToWorking_SoTheIdleWatchdogCoversTheRestOfTheTurn()
    {
        // Coda hangs AFTER the prompt is answered. If the session stayed in AwaitingPermission,
        // the idle watchdog — which only inspects Working — would never look at it again and the
        // turn would hang forever, exactly the unbounded wait this fix exists to remove.
        var (server, session) = this.NewSession(new CodaOptions { PromptIdleTimeoutSeconds = 2 });
        server.Scenario = FakeCodaScenario.PermissionThenStall;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        var stalledSignal = new TaskCompletionSource<CodaStalledEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Stalled += evt => stalledSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var requested = await permissionSignal.Task.WaitAsync(Timeout);
        Assert.True(await session.RespondAsync(requested.RequestId, "allow_once"));

        var stalled = await stalledSignal.Task.WaitAsync(Timeout);
        Assert.Equal("pending-session", stalled.SessionId);
    }

    [Fact]
    public async Task PendingRequest_IsAbandoned_WhenTheSessionCrashes()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        var errorSignal = new TaskCompletionSource<CodaErrorEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += evt => errorSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var requested = await permissionSignal.Task.WaitAsync(Timeout);

        await server.DisposeAsync();
        await errorSignal.Task.WaitAsync(Timeout);

        // Otherwise status advertises a requestId nobody can answer, and responding to it
        // would report success against a dead session.
        Assert.Null(session.PendingRequest);
        Assert.False(await session.RespondAsync(requested.RequestId, "allow_once"));
    }

    [Fact]
    public async Task PendingRequest_IsAbandoned_WhenTheSessionEnds()
    {
        var (server, session) = this.NewSession();
        server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        await session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout);
        await session.WriteUserMessageAsync("Run a command.", CancellationToken.None).WaitAsync(Timeout);

        var requested = await permissionSignal.Task.WaitAsync(Timeout);

        await session.EndAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Null(session.PendingRequest);
        Assert.False(await session.RespondAsync(requested.RequestId, "allow_once"));
    }
}
