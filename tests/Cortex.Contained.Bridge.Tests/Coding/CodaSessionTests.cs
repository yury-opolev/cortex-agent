using System.Reflection;
using System.Text.Json.Nodes;
using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Bridge.Tests.Coding.FakeCoda;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;
using StreamJsonRpc;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Unit tests for <see cref="CodaSession"/> using the internal test-seam constructor
/// (injected <see cref="CodaJsonRpcConnection"/> over in-memory streams — no real process).
/// </summary>
public sealed class CodaSessionTests : IAsyncDisposable
{
    private static readonly string[] QuestionOptions = ["A", "B"];

    private readonly (System.IO.Stream client, System.IO.Stream server) streams;
    private readonly JsonRpc serverRpc;
    private readonly CodaJsonRpcConnection connection;
    private readonly CodaSession session;
    private readonly TaskCompletionSource<string> steerReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CodaSessionTests()
    {
        var pair = FullDuplexStream.CreatePair();
        this.streams = (pair.Item1, pair.Item2);

        var formatter = new SystemTextJsonFormatter();
        this.serverRpc = new JsonRpc(
            new HeaderDelimitedMessageHandler(this.streams.server, this.streams.server, formatter));

        // Default initialize handler: registered via a real method whose sessionId param is OPTIONAL,
        // so an omitted (new-session) named param binds to null (delegates don't expose optionals).
        this.serverRpc.AddLocalRpcMethod(
            "initialize",
            typeof(CodaSessionTests).GetMethod(nameof(InitializeRpc), BindingFlags.Static | BindingFlags.NonPublic)!,
            null);

        // session/steer: capture the steering comment so SteerAsync can be asserted.
        this.serverRpc.AddLocalRpcMethod(
            "session/steer",
            new Func<string, JsonNode?>(text =>
            {
                this.steerReceived.TrySetResult(text);
                return new JsonObject { ["ok"] = true };
            }));

        this.serverRpc.StartListening();

        this.connection = new CodaJsonRpcConnection(this.streams.client, this.streams.client);

        this.session = new CodaSession(
            "test-session",
            "ch-1",
            "C:\\repos\\test",
            CodingPolicy.Prompt,
            this.connection,
            NullLogger<CodaSession>.Instance);
    }

    // initialize handler with an OPTIONAL sessionId so an omitted (new-session) named param binds to null.
    private static JsonObject InitializeRpc(string protocolVersion, string? sessionId = null)
    {
        return new JsonObject { ["protocolVersion"] = "1", ["sessionId"] = sessionId ?? Guid.NewGuid().ToString("N"), ["serverInfo"] = "fake-coda" };
    }

    public async ValueTask DisposeAsync()
    {
        await this.session.DisposeAsync();
        this.serverRpc.Dispose();
        await this.streams.client.DisposeAsync();
        await this.streams.server.DisposeAsync();
    }

    // Verify that the server can invoke methods on the client (smoke test).
    [Fact]
    public async Task ServerCanInvoke_permission_on_client()
    {
        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // Wire an immediate-allow handler directly on the connection (bypass CodaSession) to
        // confirm the plumbing works before testing the higher-level seam.
        this.connection.OnPermission = _ => Task.FromResult(true);

        var result = await this.serverRpc
            .InvokeWithParameterObjectAsync<JsonNode>(
                "request/permission",
                new { toolName = "Bash", inputPreview = "echo hi" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result!["allow"]!.GetValue<bool>());
    }

    // -----------------------------------------------------------------------
    // Recoverable limit (max_tokens / iteration cap) + steering
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LimitReached_notification_is_recoverable_and_raises_event()
    {
        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var tcs = new TaskCompletionSource<CodaLimitReachedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.LimitReached += e => tcs.TrySetResult(e);

        // The server emits event/limitReached as coda does on a max_tokens / iteration-cap stop.
        await this.serverRpc.NotifyWithParameterObjectAsync(
            "event/limitReached",
            new { kind = "max_tokens", message = "The response was truncated (max_tokens reached)." });

        var evt = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("max_tokens", evt.Kind);

        // Recoverable — a limit must NOT crash the session (unlike event/error → Crashed).
        Assert.NotEqual(CodingSessionState.Crashed, this.session.State);
    }

    [Fact]
    public async Task TrySteerAsync_when_not_working_returns_false_and_does_not_call_coda()
    {
        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // The session is Idle (no turn in flight) — a steer must not fire and must report it didn't
        // steer, so the caller delivers the message as a normal new turn rather than silently dropping it.
        var (steered, taskId) = await this.session.TrySteerAsync("hint", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(steered);
        Assert.Null(taskId);
        Assert.False(this.steerReceived.Task.IsCompleted); // session/steer was never sent
    }

    // -----------------------------------------------------------------------
    // StartAsync — start timeout
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_InitializeNeverResponds_ThrowsWithinTimeout()
    {
        var (server, clientStream) = FakeCoda.FakeCodaServer.Create();
        server.HangInitialize = true;
        await using var _ = server;

        var conn = new CodaJsonRpcConnection(clientStream);
        var options = new CodaOptions { StartTimeoutSeconds = 1 };
        var session = new CodaSession("s1", "c1", "X:/wf", CodingPolicy.Yolo, conn,
            NullLogger<CodaSession>.Instance, options);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.StartAsync(isResume: false, CancellationToken.None));

        // After a start timeout, the session can be disposed/ended cleanly.
        await session.DisposeAsync();
        Assert.Equal(CodingSessionState.Ended, session.State);
    }

    // -----------------------------------------------------------------------
    // StartAsync — initialize sessionId (new vs resume)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task NewSession_Initialize_OmitsSessionId()
    {
        var (server, clientStream) = FakeCodaServer.Create();
        await using var _ = server;
        var conn = new CodaJsonRpcConnection(clientStream);
        var session = new CodaSession("s-new", "c1", "X:/wf", CodingPolicy.Yolo, conn,
            NullLogger<CodaSession>.Instance, new CodaOptions());

        await session.StartAsync(isResume: false, CancellationToken.None);

        Assert.True(server.ReceivedInitialize);
        Assert.Null(server.ReceivedInitializeSessionId);
    }

    [Fact]
    public async Task ResumeSession_Initialize_SendsSessionId()
    {
        var (server, clientStream) = FakeCodaServer.Create();
        await using var _ = server;
        var conn = new CodaJsonRpcConnection(clientStream);
        var session = new CodaSession("s-resume", "c1", "X:/wf", CodingPolicy.Yolo, conn,
            NullLogger<CodaSession>.Instance, new CodaOptions());

        await session.StartAsync(isResume: true, CancellationToken.None);

        Assert.Equal("s-resume", server.ReceivedInitializeSessionId);
    }

    // -----------------------------------------------------------------------
    // Idle watchdog (Layer 3)
    // -----------------------------------------------------------------------

    private static (CodaSession Session, FakeCodaServer Server) StartedSession(CodaOptions options, FakeCodaScenario scenario)
    {
        var (server, clientStream) = FakeCodaServer.Create();
        server.Scenario = scenario;
        var conn = new CodaJsonRpcConnection(clientStream);
        var session = new CodaSession("s1", "c1", "X:/wf", CodingPolicy.Yolo, conn,
            NullLogger<CodaSession>.Instance, options);
        return (session, server);
    }

    [Fact]
    public async Task Prompt_NoActivityBeyondIdleTimeout_RaisesStalled()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.Stall);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var stalled = new TaskCompletionSource<CodaStalledEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Stalled += e => stalled.TrySetResult(e);

        await session.WriteUserMessageAsync("do work", CancellationToken.None);

        var evt = await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("stalled", evt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CodingSessionState.Crashed, session.State);
    }

    [Fact]
    public async Task StalledEvent_IncludesStderrTail()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.Stall);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);
        session.AppendStderrForTest("coda: provider auth failed");

        var stalled = new TaskCompletionSource<CodaStalledEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Stalled += e => stalled.TrySetResult(e);

        await session.WriteUserMessageAsync("work", CancellationToken.None);
        var evt = await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("provider auth failed", evt.StderrTail ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Prompt_CompletesNormally_DoesNotRaiseFrozen()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.Happy);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var errored = false;
        session.Error += _ => errored = true;
        session.Stalled += _ => errored = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += _ => done.TrySetResult();

        await session.WriteUserMessageAsync("hi", CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1500); // beyond idle window; session is back to Idle

        Assert.False(errored);
    }

    [Fact]
    public async Task Watchdog_DoesNotFireWhileAwaitingUser()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.Permission);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var frozenFired = false;
        session.Error += _ => frozenFired = true;
        session.Stalled += _ => frozenFired = true;
        var asked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PermissionRequested += e => asked.TrySetResult(e.RequestId);

        await session.WriteUserMessageAsync("touch a file", CancellationToken.None);
        var requestId = await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Task.Delay(2500); // well beyond idle window while AwaitingPermission
        Assert.False(frozenFired);

        await session.RespondAsync(requestId, "allow_once"); // unblock so the turn unwinds
    }

    [Fact]
    public async Task Prompt_ActivityWithinWindow_DoesNotFreeze()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.SlowDrip);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var errored = false;
        session.Error += _ => errored = true;
        session.Stalled += _ => errored = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += _ => done.TrySetResult();

        await session.WriteUserMessageAsync("drip work", CancellationToken.None);

        // The scenario drips activity (~400ms spacing) across more than one 1s window
        // before completing; steady activity must keep the watchdog from firing.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(errored);
        Assert.Equal(CodingSessionState.Idle, session.State);
    }

    [Fact]
    public async Task StreamProgress_KeepsSessionAlive_AndExposesSnapshot()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.SlowStream);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var errored = false;
        session.Error += _ => errored = true;
        session.Stalled += _ => errored = true;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += _ => done.TrySetResult();

        await session.WriteUserMessageAsync("stream please", CancellationToken.None);

        // The stream-only pulses (spaced ~400ms across >2s) keep the 1s watchdog from firing,
        // exactly the production case (coda mid-LLM-call) the watchdog used to be blind to.
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(errored);
        Assert.Equal(CodingSessionState.Idle, session.State);
        Assert.NotNull(session.LastStreamActivityAt);
        Assert.True((session.StreamedChars ?? 0) > 0);
    }

    [Fact]
    public async Task Watchdog_OnStall_RaisesStalledEventWithContext()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 1 }, FakeCodaScenario.Stall);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);
        session.AppendStderrForTest("coda: awaiting model response");

        var stalled = new TaskCompletionSource<CodaStalledEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Stalled += e => stalled.TrySetResult(e);

        await session.WriteUserMessageAsync("work", CancellationToken.None);

        var evt = await stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("s1", evt.SessionId);
        Assert.True(evt.IdleSeconds >= 1);
        Assert.Contains("awaiting model response", evt.StderrTail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CodingSessionState.Crashed, session.State);
    }

    [Fact]
    public async Task GoalRun_PopulatesGoalStatusFromPromptResult()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 30 }, FakeCodaScenario.Goal);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FinalResult += _ => done.TrySetResult();

        await session.WriteUserMessageAsync("reach the goal", CancellationToken.None);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The prompt result carries goalStatus; capture happens just after PromptAsync returns,
        // which may land slightly after FinalResult — poll briefly.
        CodingGoalStatus? gs = null;
        for (var i = 0; i < 50 && gs is null; i++)
        {
            gs = session.GoalStatus;
            if (gs is null)
            {
                await Task.Delay(20);
            }
        }

        Assert.NotNull(gs);
        Assert.Equal("Met", gs!.Outcome);
        Assert.Equal(3, gs.Continuations);
        Assert.Equal(42.5, gs.ElapsedSeconds);
    }

    [Fact]
    public async Task SetGoalAsync_ForwardsToCoda_AndReturnsEchoedConfig()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 30 }, FakeCodaScenario.Happy);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var result = await session.SetGoalAsync("all tests pass", "30m", 200, CancellationToken.None);

        Assert.Equal("all tests pass", result.Goal);
        Assert.Equal("30m", result.MaxDuration);
        Assert.Equal(200, result.MaxContinuations);
        Assert.Equal(("all tests pass", "30m", (int?)200), server.LastSetGoal);
    }

    [Fact]
    public async Task SetGoalAsync_EmptyGoal_ClearsGoal()
    {
        var (session, server) = StartedSession(new CodaOptions { PromptIdleTimeoutSeconds = 30 }, FakeCodaScenario.Happy);
        await using var _ = server;
        await session.StartAsync(isResume: false, CancellationToken.None);

        var result = await session.SetGoalAsync(null, null, null, CancellationToken.None);

        Assert.Null(result.Goal);
        Assert.Equal((null, null, (int?)null), server.LastSetGoal);
    }

    // -----------------------------------------------------------------------
    // RespondAsync — permission
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RespondAsync_allow_once_resolves_permission_tcs_true()
    {
        var eventSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.PermissionRequested += evt => eventSignal.TrySetResult(evt.RequestId);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var permissionTask = this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/permission",
            new { toolName = "Bash", inputPreview = "rm -rf /tmp/x" });

        var capturedRequestId = await eventSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await this.session.RespondAsync(capturedRequestId, "allow_once");

        var reply = await permissionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reply!["allow"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RespondAsync_deny_resolves_permission_tcs_false()
    {
        var eventSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.PermissionRequested += evt => eventSignal.TrySetResult(evt.RequestId);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var permissionTask = this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/permission",
            new { toolName = "Edit", inputPreview = "edit file.cs" });

        var capturedRequestId = await eventSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await this.session.RespondAsync(capturedRequestId, "deny");

        var reply = await permissionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reply!["allow"]!.GetValue<bool>());
    }

    // -----------------------------------------------------------------------
    // RespondAsync — plan approval
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RespondAsync_approve_resolves_plan_tcs_true()
    {
        var eventSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.PlanApproval += evt => eventSignal.TrySetResult(evt.RequestId);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var planTask = this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/planApproval",
            new { plan = "Step 1: do X\nStep 2: do Y" });

        var capturedRequestId = await eventSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await this.session.RespondAsync(capturedRequestId, "approve");

        var reply = await planTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reply!["approve"]!.GetValue<bool>());
    }

    // -----------------------------------------------------------------------
    // RespondAsync — question
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RespondAsync_answer_resolves_question_tcs_with_string()
    {
        var eventSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.Question += evt => eventSignal.TrySetResult(evt.RequestId);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        var questionTask = this.serverRpc.InvokeWithParameterObjectAsync<JsonNode>(
            "request/question",
            new { question = "Which approach?", options = QuestionOptions, multiSelect = false });

        var capturedRequestId = await eventSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await this.session.RespondAsync(capturedRequestId, "A");

        var reply = await questionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("A", reply!["answer"]!.GetValue<string>());
    }
}

/// <summary>
/// Regression for the coda "initialize hangs / start times out" production bug: spawning coda
/// must NOT set StandardInputEncoding/StandardOutputEncoding, because stdin/stdout are driven as
/// raw BaseStream by StreamJsonRpc. Setting an output encoding attaches a StreamReader that
/// swallows the child's stdout so the raw BaseStream read sees EOF and the handshake never
/// completes. Proven via the SPIKES/coda-handshake spike (initialize 30s timeout -> 174ms once
/// the encodings were removed).
/// </summary>
public sealed class CodaSessionProcessStartInfoTests
{
    [Fact]
    public void BuildProcessStartInfo_LeavesStdInOutEncodingUnset_KeepsStderrEncoding()
    {
        string[] args = ["serve", "--telemetry", "--provider", "github-copilot"];
        var psi = CodaSession.BuildProcessStartInfo("coda", @"C:\wf", args);

        Assert.Null(psi.StandardInputEncoding);   // raw BaseStream — must stay unset
        Assert.Null(psi.StandardOutputEncoding);  // raw BaseStream — must stay unset (the bug)
        Assert.Equal(System.Text.Encoding.UTF8, psi.StandardErrorEncoding); // read via StreamReader
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal("coda", psi.FileName);
        Assert.Equal(@"C:\wf", psi.WorkingDirectory);
        Assert.Contains("serve", psi.ArgumentList);
        Assert.Contains("--telemetry", psi.ArgumentList);
    }
}
