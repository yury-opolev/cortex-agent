using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Tests the pure hold-back policy: given a commit's confidence and the current
/// grace state, should we dispatch now, keep holding, or force-dispatch (cap)?
/// </summary>
public class HoldBackDecisionTests
{
    [Fact]
    public void Decide_Confident_DispatchesImmediately()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Confident,
            graceUsed: false,
            accumulatedMs: 1000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 0,
            graceWindowMs: 350);

        Assert.Equal(HoldBackOutcome.DispatchNow, outcome);
    }

    [Fact]
    public void Decide_Tentative_WithinGrace_Holds()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Tentative,
            graceUsed: false,
            accumulatedMs: 1000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 100,
            graceWindowMs: 350);

        Assert.Equal(HoldBackOutcome.Hold, outcome);
    }

    [Fact]
    public void Decide_Tentative_GraceExpired_Dispatches()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Tentative,
            graceUsed: false,
            accumulatedMs: 1000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 400,
            graceWindowMs: 350);

        Assert.Equal(HoldBackOutcome.DispatchNow, outcome);
    }

    [Fact]
    public void Decide_Tentative_GraceUsed_DispatchesImmediately()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Tentative,
            graceUsed: true,
            accumulatedMs: 1000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 0,
            graceWindowMs: 350);

        Assert.Equal(HoldBackOutcome.DispatchNow, outcome);
    }

    [Fact]
    public void Decide_MaxUtteranceExceeded_ForceDispatches()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Tentative,
            graceUsed: false,
            accumulatedMs: 20000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 0,
            graceWindowMs: 350);

        Assert.Equal(HoldBackOutcome.ForceDispatch, outcome);
    }

    [Fact]
    public void Decide_MaxUtteranceExceeded_OverridesConfident()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Confident,
            graceUsed: false,
            accumulatedMs: 25000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 0,
            graceWindowMs: 350);

        Assert.Equal(HoldBackOutcome.ForceDispatch, outcome);
    }

    [Fact]
    public void Decide_ZeroGraceWindow_DispatchesImmediately()
    {
        var outcome = HoldBackDecision.Decide(
            CommitConfidence.Tentative,
            graceUsed: false,
            accumulatedMs: 1000,
            maxUtteranceMs: 20000,
            graceElapsedMs: 0,
            graceWindowMs: 0);

        Assert.Equal(HoldBackOutcome.DispatchNow, outcome);
    }
}
