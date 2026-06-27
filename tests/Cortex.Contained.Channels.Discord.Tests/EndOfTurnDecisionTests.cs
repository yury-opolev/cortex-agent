using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Tests the "should we commit this utterance now?" decision that gates
/// end-of-utterance dispatch in the Discord voice handler. Pure static helper
/// so it's testable without spinning up Discord / audio / STT.
/// </summary>
public class EndOfTurnDecisionTests
{
    // ── traditional behavior: turn detector disabled → only silence timeout matters ──

    [Fact]
    public void Decide_DetectorOff_AndSilenceBelowTimeout_DoesNotCommit()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 800,
            silenceTimeoutMs: 1500,
            useTurnDetector: false,
            pEou: 0f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.False(result.Commit);
        Assert.Equal(CommitReason.NotYet, result.Reason);
    }

    [Fact]
    public void Decide_DetectorOff_AndSilenceAtTimeout_Commits_ReasonSilenceTimeout()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1500,
            silenceTimeoutMs: 1500,
            useTurnDetector: false,
            pEou: 0.95f,    // ignored
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitReason.SilenceTimeout, result.Reason);
    }

    // ── smart behavior: detector on, confident END signal → early commit ──

    [Fact]
    public void Decide_DetectorOn_ConfidentEnd_AndMinSilenceElapsed_CommitsEarly_ReasonTurnDetector()
    {
        // MinEarlyCommitSilenceMs is 500ms — use exactly 500ms to confirm the floor.
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.88f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitReason.TurnDetectorEarly, result.Reason);
    }

    [Fact]
    public void Decide_DetectorOn_ConfidentEnd_ButMinSilenceNotYetElapsed_DoesNotCommit()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 100,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.99f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.False(result.Commit);
        Assert.Equal(CommitReason.NotYet, result.Reason);
    }

    // ── smart behavior: detector on, low confidence → wait longer ──

    [Fact]
    public void Decide_DetectorOn_LowConfidence_WaitsForFallbackTimeout()
    {
        // Below base timeout: not yet.
        var early = EndOfTurnDecision.Decide(
            silenceElapsedMs: 500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.001f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.False(early.Commit);
        Assert.Equal(CommitReason.NotYet, early.Reason);

        // At base timeout with very-low pEou: the extension kicks in — still not yet.
        var atBase = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.001f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.False(atBase.Commit);
        Assert.Equal(CommitReason.NotYet, atBase.Reason);

        // At hard cap: unconditional commit regardless of detector state.
        var fallback = EndOfTurnDecision.Decide(
            silenceElapsedMs: 6000,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.001f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(fallback.Commit);
        Assert.Equal(CommitReason.SilenceTimeout, fallback.Reason);
    }

    [Fact]
    public void Decide_DetectorOn_BorderlineConfidence_DoesNotCommitEarly()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.011f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.False(result.Commit);
        Assert.Equal(CommitReason.NotYet, result.Reason);
    }

    [Fact]
    public void Decide_DetectorOn_JustAboveThreshold_CommitsEarly()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.012f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitReason.TurnDetectorEarly, result.Reason);
    }

    // ── detector can never DELAY beyond silenceTimeoutMs ──

    [Fact]
    public void Decide_DetectorOn_SilenceAboveFallback_AlwaysCommits_SilenceTimeoutWins()
    {
        // If silence is already over the fallback, we credit the timeout — not
        // the detector — even if pEou happens to be high too. Keeps causality
        // honest in telemetry: timeout is the unconditional ceiling.
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1600,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.99f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitReason.SilenceTimeout, result.Reason);
    }

    // ── new: bidirectional adaptation ──

    [Fact]
    public void Decide_BaseTimeoutReachedButPEouVeryLow_ExtendsWaiting()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1600,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.001f,
            threshold: 0.03f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);
        Assert.False(result.Commit);
        Assert.Equal(CommitReason.NotYet, result.Reason);
    }

    [Fact]
    public void Decide_HardCapReached_CommitsEvenIfPEouLow()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 6100,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.001f,
            threshold: 0.03f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);
        Assert.True(result.Commit);
        Assert.Equal(CommitReason.SilenceTimeout, result.Reason);
    }

    [Fact]
    public void Decide_BaseTimeoutReached_PEouMidConfidence_CommitsNormally()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1600,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.015f,
            threshold: 0.03f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);
        Assert.True(result.Commit);
        Assert.Equal(CommitReason.SilenceTimeout, result.Reason);
    }

    [Fact]
    public void Decide_MinEarlyCommitSilenceFloorIsNow300ms()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 200,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.5f,
            threshold: 0.03f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);
        Assert.False(result.Commit);
        Assert.Equal(CommitReason.NotYet, result.Reason);
    }

    [Fact]
    public void Decide_EarlyCommit_AtExactly300msSilence_Succeeds()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 300,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.5f,
            threshold: 0.03f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);
        Assert.True(result.Commit);
        Assert.Equal(CommitReason.TurnDetectorEarly, result.Reason);
    }

    // ── commit confidence (hold-back re-absorb) ──

    [Fact]
    public void Decide_HardCeiling_IsConfident()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 6000,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.001f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitConfidence.Confident, result.Confidence);
    }

    [Fact]
    public void Decide_BaseTimeout_DetectorOff_IsTentative()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1500,
            silenceTimeoutMs: 1500,
            useTurnDetector: false,
            pEou: 0f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitConfidence.Tentative, result.Confidence);
    }

    [Fact]
    public void Decide_BaseTimeout_DetectorOn_ZeroPEou_IsTentative()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitConfidence.Tentative, result.Confidence);
    }

    [Fact]
    public void Decide_BaseTimeout_DetectorOn_PEouAboveLowConfidence_IsConfident()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 1500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.02f,
            threshold: 0.5f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitConfidence.Confident, result.Confidence);
    }

    [Fact]
    public void Decide_TurnDetectorEarly_IsConfident()
    {
        var result = EndOfTurnDecision.Decide(
            silenceElapsedMs: 500,
            silenceTimeoutMs: 1500,
            useTurnDetector: true,
            pEou: 0.88f,
            threshold: 0.011f,
            lowConfidenceThreshold: 0.005f,
            maxSilenceTimeoutMs: 6000);

        Assert.True(result.Commit);
        Assert.Equal(CommitReason.TurnDetectorEarly, result.Reason);
        Assert.Equal(CommitConfidence.Confident, result.Confidence);
    }
}
