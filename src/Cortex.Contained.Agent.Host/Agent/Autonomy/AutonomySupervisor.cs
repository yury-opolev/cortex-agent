using System.Globalization;
using System.Text;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Agent.Autonomy;

/// <summary>What the supervisor wants the caller to do after a mid-loop check.</summary>
internal enum MidLoopAction
{
    /// <summary>Nothing to report — keep running the current loop.</summary>
    Continue,

    /// <summary>Deliver <see cref="MidLoopDecision.Nudge"/> to the model, then keep running.</summary>
    Nudge,

    /// <summary>Stop the run; <see cref="MidLoopDecision.Outcome"/> says why.</summary>
    Stop,
}

/// <summary>Outcome of a mid-loop check, evaluated after every tool round.</summary>
internal sealed record MidLoopDecision(MidLoopAction Action, GoalOutcome Outcome, string Nudge)
{
    public static MidLoopDecision Continue { get; } = new(MidLoopAction.Continue, GoalOutcome.None, string.Empty);
}

/// <summary>
/// Ties the autonomy pieces together for one goal-driven run: the completion judge decides whether
/// the goal is met, the budget is a backstop, and the stuck detector plus termination proof end a
/// run that is looping or provably blocked.
/// </summary>
/// <remarks>
/// <para>
/// THE MID-LOOP GATE. <see cref="EvaluateCompletionAsync"/> is only reachable when a turn calls no
/// tools. An agent that calls a tool every single turn would never reach it, so every budget and
/// stuck check behind it would be dead code — the bug coda shipped and had to fix. This is why
/// <see cref="CheckMidLoop"/> exists and must be called after every tool round.
/// </para>
/// <para>
/// The completion judge is deliberately NOT part of the mid-loop check: it is an LLM call, and
/// running it per tool round would cost a model call for every step of the run.
/// </para>
/// </remarks>
internal sealed partial class AutonomySupervisor
{
    private readonly string goal;
    private readonly GoalBudget budget;
    private readonly CompletionJudge judge;
    private readonly StuckDetector stuckDetector;
    private readonly AssumptionLedger ledger;
    private readonly ILogger logger;

    private bool loopingSinceLastCheck;

    public AutonomySupervisor(
        string goal,
        GoalBudget budget,
        CompletionJudge judge,
        StuckDetector stuckDetector,
        AssumptionLedger ledger,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(stuckDetector);
        ArgumentNullException.ThrowIfNull(ledger);

        this.goal = goal;
        this.budget = budget;
        this.judge = judge;
        this.stuckDetector = stuckDetector;
        this.ledger = ledger;
        this.logger = logger;
    }

    public GoalBudget Budget => this.budget;

    public AssumptionLedger Ledger => this.ledger;

    /// <summary>
    /// Budget and loop checks, cheap enough to run after every tool round. Never calls the model.
    /// </summary>
    public MidLoopDecision CheckMidLoop(StuckDetectorTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (this.budget.IsExhausted)
        {
            this.LogBudgetExhausted(this.budget.Consumed.Elapsed, this.budget.Consumed.ContinuationsUsed);
            return new MidLoopDecision(MidLoopAction.Stop, GoalOutcome.BudgetExhausted, string.Empty);
        }

        var detection = this.stuckDetector.ObserveTurn(turn);
        if (detection.Kind == StuckDetectionKind.None)
        {
            this.loopingSinceLastCheck = false;
            return MidLoopDecision.Continue;
        }

        // A detected loop is nudged once with a description of the loop rather than killed
        // outright — a run that can see the loop it is in will often break out of it. The
        // detector suppresses a repeat nudge for the same (pattern, action) pair, so a second
        // detection means the nudge did not work and the run is genuinely stuck.
        if (this.loopingSinceLastCheck)
        {
            this.LogStuckAfterNudge(detection.Description);
            return new MidLoopDecision(MidLoopAction.Stop, GoalOutcome.Stalled, string.Empty);
        }

        this.loopingSinceLastCheck = true;
        this.LogNudging(detection.Description);
        return new MidLoopDecision(MidLoopAction.Nudge, GoalOutcome.None, detection.Description);
    }

    /// <summary>
    /// Decides whether a finished loop actually met the goal. Called only where a turn produced
    /// final text with no tool calls.
    /// </summary>
    public async Task<GoalVerdict> EvaluateCompletionAsync(
        IReadOnlyList<LlmMessage> transcript,
        TerminationProofInput proofInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(proofInput);

        if (this.budget.IsExhausted)
        {
            this.LogBudgetExhausted(this.budget.Consumed.Elapsed, this.budget.Consumed.ContinuationsUsed);
            return GoalVerdict.Stop(GoalOutcome.BudgetExhausted, this.BuildReport("Budget exhausted."));
        }

        // Observable proof outranks the judge. "Every remaining item is parked behind a blocker
        // that was actually tried" is a fact; the judge's opinion is not.
        var proof = TerminationProof.Evaluate(proofInput);
        if (proof == TerminationProofResult.GenuinelyBlocked)
        {
            return GoalVerdict.Stop(GoalOutcome.GenuinelyBlocked, this.BuildReport("Every remaining item is blocked."));
        }

        if (proof == TerminationProofResult.Stalled)
        {
            return GoalVerdict.Stop(GoalOutcome.Stalled, this.BuildReport("No observable progress and nothing left to advance."));
        }

        var verdict = await this.judge.EvaluateAsync(this.goal, transcript, cancellationToken).ConfigureAwait(false);
        if (verdict is GoalVerdict.StopVerdict stop)
        {
            return GoalVerdict.Stop(stop.Outcome, this.BuildReport(stop.Report));
        }

        this.budget.RecordContinuation();
        return verdict;
    }

    /// <summary>
    /// The run's final report: the reason it stopped, what it spent, and every decision it made
    /// instead of asking a human. The ledger is the point — an unattended multi-day run has to be
    /// reviewable afterwards, not just confidently narrated.
    /// </summary>
    private string BuildReport(string reason)
    {
        var consumed = this.budget.Consumed;
        var entries = this.ledger.Snapshot();

        var report = new StringBuilder();
        report.Append(reason);
        report.Append(CultureInfo.InvariantCulture, $" Elapsed {consumed.Elapsed:g}, continuations {consumed.ContinuationsUsed}.");

        if (entries.Count == 0)
        {
            return report.ToString();
        }

        report.AppendLine();
        report.AppendLine("Decisions made without asking:");
        foreach (var entry in entries)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"- [{entry.Kind}] {entry.WorkItem}: {entry.Decision ?? entry.Blocker ?? entry.Answer}");
        }

        return report.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "[autonomy] Budget exhausted after {Elapsed} and {Continuations} continuations")]
    private partial void LogBudgetExhausted(TimeSpan elapsed, int continuations);

    [LoggerMessage(Level = LogLevel.Information, Message = "[autonomy] Nudging a stuck run: {Description}")]
    private partial void LogNudging(string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "[autonomy] Run still stuck after a nudge, stopping: {Description}")]
    private partial void LogStuckAfterNudge(string description);
}
