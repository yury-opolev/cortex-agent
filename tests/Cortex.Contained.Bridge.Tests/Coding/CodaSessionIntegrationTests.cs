using Cortex.Contained.Bridge.Coding;
using Cortex.Contained.Bridge.Tests.Coding.FakeCoda;
using Cortex.Contained.Contracts.Coding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Bridge.Tests.Coding;

/// <summary>
/// Integration tests that pair a real <see cref="CodaJsonRpcConnection"/> (and
/// <see cref="CodaSession"/>) against a <see cref="FakeCodaServer"/> over an in-memory
/// duplex stream pair.  No real process is spawned.
/// </summary>
public sealed class CodaSessionIntegrationTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeCodaServer server;
    private readonly CodaJsonRpcConnection connection;
    private readonly CodaSession session;

    public CodaSessionIntegrationTests()
    {
        var (fakeCoda, clientStream) = FakeCodaServer.Create();
        this.server = fakeCoda;

        this.connection = new CodaJsonRpcConnection(clientStream, clientStream);

        this.session = new CodaSession(
            "integration-session",
            "ch-integration",
            "C:\\repos\\test",
            CodingPolicy.Prompt,
            this.connection,
            NullLogger<CodaSession>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.session.DisposeAsync();
        await this.server.DisposeAsync();
    }

    // -----------------------------------------------------------------------
    // Init failure (fast-fail)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_InitializeFails_ThrowsCarryingCodaReason()
    {
        this.server.FailInitializeMessage = "provider/model unavailable: 400 model_not_supported";

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => this.session.StartAsync(isResume: false, CancellationToken.None).WaitAsync(Timeout));

        Assert.Contains("model_not_supported", ex.Message);
    }

    // -----------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_WriteUserMessage_raises_FinalResult_within_5s()
    {
        this.server.Scenario = FakeCodaScenario.Happy;
        this.server.AssistantText = "Here is your answer.";

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(Timeout);

        await this.session.WriteUserMessageAsync("Do something.", CancellationToken.None)
            .WaitAsync(Timeout);

        var result = await finalResultSignal.Task.WaitAsync(Timeout);

        Assert.Equal("integration-session", result.SessionId);
        Assert.Equal("Here is your answer.", result.FinalText);
        Assert.NotEmpty(result.ToolCalls);
    }

    // -----------------------------------------------------------------------
    // Status snapshot surface: telemetry log path + usage totals
    // -----------------------------------------------------------------------

    [Fact]
    public async Task HappyPath_session_captures_telemetry_log_path_and_usage_totals()
    {
        this.server.Scenario = FakeCodaScenario.Happy;
        this.server.TelemetryLogPath = "/tmp/coda/telemetry-xyz.log";

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.Equal("/tmp/coda/telemetry-xyz.log", this.session.TelemetryLogPath);

        await this.session.WriteUserMessageAsync("Do something.", CancellationToken.None)
            .WaitAsync(Timeout);

        await finalResultSignal.Task.WaitAsync(Timeout);

        Assert.Equal(1234L, this.session.InputTokens);
        Assert.Equal(567L, this.session.OutputTokens);
    }

    // -----------------------------------------------------------------------
    // Permission scenario
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Permission_server_raises_permission_event_and_completes_after_allow()
    {
        this.server.Scenario = FakeCodaScenario.Permission;

        var permissionSignal = new TaskCompletionSource<CodaPermissionRequestEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.PermissionRequested += evt => permissionSignal.TrySetResult(evt);

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(Timeout);

        await this.session.WriteUserMessageAsync("Run a command.", CancellationToken.None)
            .WaitAsync(Timeout);

        var permissionEvt = await permissionSignal.Task.WaitAsync(Timeout);

        Assert.Equal("Bash", permissionEvt.ToolName);
        Assert.Equal("integration-session", permissionEvt.SessionId);
        Assert.False(string.IsNullOrEmpty(permissionEvt.RequestId));

        await this.session.RespondAsync(permissionEvt.RequestId, "allow_once");

        var finalResult = await finalResultSignal.Task.WaitAsync(Timeout);
        Assert.Equal("integration-session", finalResult.SessionId);
    }

    // -----------------------------------------------------------------------
    // Question scenario
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Question_server_raises_question_event_and_completes_after_answer()
    {
        this.server.Scenario = FakeCodaScenario.Question;

        var questionSignal = new TaskCompletionSource<CodaQuestionEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.Question += evt => questionSignal.TrySetResult(evt);

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(Timeout);

        await this.session.WriteUserMessageAsync("What approach?", CancellationToken.None)
            .WaitAsync(Timeout);

        var questionEvt = await questionSignal.Task.WaitAsync(Timeout);

        Assert.Equal("Which approach?", questionEvt.Question);
        Assert.Equal(["A", "B"], questionEvt.Options);
        Assert.False(questionEvt.MultiSelect);
        Assert.False(string.IsNullOrEmpty(questionEvt.RequestId));

        await this.session.RespondAsync(questionEvt.RequestId, "A");

        var finalResult = await finalResultSignal.Task.WaitAsync(Timeout);
        Assert.Equal("integration-session", finalResult.SessionId);
    }

    // -----------------------------------------------------------------------
    // Plan approval scenario
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Plan_server_raises_plan_event_and_completes_after_approve()
    {
        this.server.Scenario = FakeCodaScenario.Plan;

        var planSignal = new TaskCompletionSource<CodaPlanApprovalEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.PlanApproval += evt => planSignal.TrySetResult(evt);

        var finalResultSignal = new TaskCompletionSource<CodaFinalResultEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.FinalResult += evt => finalResultSignal.TrySetResult(evt);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(Timeout);

        await this.session.WriteUserMessageAsync("Execute the plan.", CancellationToken.None)
            .WaitAsync(Timeout);

        var planEvt = await planSignal.Task.WaitAsync(Timeout);

        Assert.Contains("Step 1", planEvt.Plan);
        Assert.False(string.IsNullOrEmpty(planEvt.RequestId));

        await this.session.RespondAsync(planEvt.RequestId, "approve");

        var finalResult = await finalResultSignal.Task.WaitAsync(Timeout);
        Assert.Equal("integration-session", finalResult.SessionId);
    }

    // -----------------------------------------------------------------------
    // Crash scenario
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Crash_server_drops_connection_raises_Error_event()
    {
        this.server.Scenario = FakeCodaScenario.Crash;

        var errorSignal = new TaskCompletionSource<CodaErrorEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        this.session.Error += evt => errorSignal.TrySetResult(evt);

        await this.session.StartAsync(isResume: false, CancellationToken.None)
            .WaitAsync(Timeout);

        await this.session.WriteUserMessageAsync("Do something.", CancellationToken.None)
            .WaitAsync(Timeout);

        var errorEvt = await errorSignal.Task.WaitAsync(Timeout);

        Assert.Equal("integration-session", errorEvt.SessionId);
        Assert.False(string.IsNullOrEmpty(errorEvt.Message));
        Assert.Equal(errorEvt.Message, this.session.LastError);
    }
}
