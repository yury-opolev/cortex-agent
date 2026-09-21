using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Agent.Autonomy;
using Cortex.Contained.Contracts.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests;

public sealed class AutonomySupervisorTests
{
    private const string Goal = "make the build green";

    // ── Mid-loop gate ────────────────────────────────────────────────
    // This gate is why the feature terminates at all: EvaluateCompletionAsync is only reachable
    // when a turn calls NO tools, so a run that calls a tool every turn would otherwise never be
    // budget- or loop-checked.

    [Fact]
    public void CheckMidLoop_BudgetExhausted_StopsWithBudgetExhausted()
    {
        // Rehydrated at the continuation ceiling — the state a resumed run reaches after using up
        // its budget. Budgets may only be overridden upward, so exhaustion is reached by consuming
        // the default rather than by configuring a small one.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var budget = GoalBudget.Rehydrate(
            new GoalBudgetConsumed(TimeSpan.Zero, GoalBudget.DefaultContinuationLimit),
            clock);
        var supervisor = Create(budget, out _);

        var decision = supervisor.CheckMidLoop(StuckDetectorTurn.Monologue("thinking"));

        Assert.Equal(MidLoopAction.Stop, decision.Action);
        Assert.Equal(GoalOutcome.BudgetExhausted, decision.Outcome);
    }

    [Fact]
    public void CheckMidLoop_NoLoopDetected_Continues()
    {
        var supervisor = Create(out _);

        var decision = supervisor.CheckMidLoop(Turn("read_file", "a.txt"));

        Assert.Equal(MidLoopAction.Continue, decision.Action);
    }

    [Fact]
    public void CheckMidLoop_LoopDetected_NudgesBeforeStopping()
    {
        var supervisor = Create(out _);

        // Repeat one action until the detector trips; the first detection must NUDGE, not kill —
        // a run that can see its own loop will often break out of it.
        MidLoopDecision? firstDetection = null;
        for (var i = 0; i < 8 && firstDetection is null; i++)
        {
            var decision = supervisor.CheckMidLoop(Turn("grep", "same-args"));
            if (decision.Action != MidLoopAction.Continue)
            {
                firstDetection = decision;
            }
        }

        Assert.NotNull(firstDetection);
        Assert.Equal(MidLoopAction.Nudge, firstDetection!.Action);
        Assert.NotEmpty(firstDetection.Nudge);
    }

    // ── Completion ───────────────────────────────────────────────────

    [Fact]
    public async Task EvaluateCompletionAsync_EveryRemainingItemParked_StopsGenuinelyBlockedWithoutCallingJudge()
    {
        var llm = Substitute.For<ILlmClient>();
        var supervisor = Create(out var ledger, llm);
        ledger.ParkBlocker("item-1", "no credentials", "tried the stored token and the env var");
        var parkedKeys = ledger.ParkedBlockerItemKeys().ToList();

        var verdict = await supervisor.EvaluateCompletionAsync(
            Transcript(),
            new TerminationProofInput(parkedKeys, parkedKeys, IsLooping: false, NothingLeftToAdvance: false, []),
            CancellationToken.None);

        var stop = Assert.IsType<GoalVerdict.StopVerdict>(verdict);
        Assert.Equal(GoalOutcome.GenuinelyBlocked, stop.Outcome);

        // Observable proof outranks opinion: the judge must not even be consulted.
        await llm.DidNotReceive().CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EvaluateCompletionAsync_JudgeSaysContinue_RecordsAContinuation()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Reply("CONTINUE: still need to fix the failing test"));
        var supervisor = Create(out _, llm);

        var verdict = await supervisor.EvaluateCompletionAsync(Transcript(), KeepGoing(), CancellationToken.None);

        var cont = Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
        Assert.Contains("failing test", cont.Remaining, StringComparison.Ordinal);
        Assert.Equal(1, supervisor.Budget.Consumed.ContinuationsUsed);
    }

    [Fact]
    public async Task EvaluateCompletionAsync_JudgeThrows_FailsOpenAndDoesNotStop()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<LlmCompletionResult>>(_ => throw new InvalidOperationException("provider down"));
        var supervisor = Create(out _, llm);

        var verdict = await supervisor.EvaluateCompletionAsync(Transcript(), KeepGoing(), CancellationToken.None);

        // A broken judge must never be able to declare success.
        Assert.IsType<GoalVerdict.ContinueVerdict>(verdict);
    }

    [Fact]
    public async Task EvaluateCompletionAsync_JudgeSaysDone_ReportIncludesLedgerDecisions()
    {
        var llm = Substitute.For<ILlmClient>();
        llm.CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>()).Returns(Reply("DONE"));
        var supervisor = Create(out var ledger, llm);
        ledger.RecordAssumption("database choice", "used sqlite", "no server was configured");

        var verdict = await supervisor.EvaluateCompletionAsync(Transcript(), KeepGoing(), CancellationToken.None);

        var stop = Assert.IsType<GoalVerdict.StopVerdict>(verdict);
        Assert.Equal(GoalOutcome.Met, stop.Outcome);

        // The ledger IS the report — an unattended run has to be reviewable, not just narrated.
        Assert.Contains("used sqlite", stop.Report, StringComparison.Ordinal);
        Assert.Contains("database choice", stop.Report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateCompletionAsync_BudgetExhausted_StopsWithoutCallingJudge()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var budget = GoalBudget.Rehydrate(
            new GoalBudgetConsumed(GoalBudget.DefaultWallClockLimit, 0),
            clock);
        var llm = Substitute.For<ILlmClient>();
        var supervisor = Create(budget, out _, llm);

        var verdict = await supervisor.EvaluateCompletionAsync(Transcript(), KeepGoing(), CancellationToken.None);

        var stop = Assert.IsType<GoalVerdict.StopVerdict>(verdict);
        Assert.Equal(GoalOutcome.BudgetExhausted, stop.Outcome);
        await llm.DidNotReceive().CompleteAsync(Arg.Any<LlmCompletionRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AutonomySupervisor Create(out AssumptionLedger ledger, ILlmClient? llm = null)
        => Create(GoalBudget.Create(new FakeTimeProvider(DateTimeOffset.UnixEpoch)), out ledger, llm);

    private static AutonomySupervisor Create(GoalBudget budget, out AssumptionLedger ledger, ILlmClient? llm = null)
    {
        llm ??= Substitute.For<ILlmClient>();
        var modelProvider = Substitute.For<IModelProvider>();
        modelProvider.DefaultModel.Returns("test-model");
        ledger = new AssumptionLedger();

        return new AutonomySupervisor(
            Goal,
            budget,
            new CompletionJudge(llm, modelProvider, NullLogger<CompletionJudge>.Instance),
            new StuckDetector(),
            ledger,
            NullLogger.Instance);
    }

    private static TerminationProofInput KeepGoing()
        => new([], [], IsLooping: false, NothingLeftToAdvance: false, []);

    private static IReadOnlyList<LlmMessage> Transcript()
        => [new LlmMessage { Role = "assistant", Content = "I did some work." }];

    private static StuckDetectorTurn Turn(string tool, string args)
        => StuckDetectorTurn.WithActions(
            [new StuckDetectorAction(tool, args, "thinking", "same observation", IsError: false)],
            "thinking");

    private static Task<LlmCompletionResult> Reply(string content)
        => Task.FromResult(new LlmCompletionResult { Success = true, Content = content });

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => this.now;

        public void Advance(TimeSpan by) => this.now += by;
    }
}
