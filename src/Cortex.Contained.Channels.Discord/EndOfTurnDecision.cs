namespace Cortex.Contained.Channels.Discord;

/// <summary>Why (or why not) did we just decide to commit?</summary>
public enum CommitReason
{
    /// <summary>We are not committing yet — keep waiting for silence.</summary>
    NotYet,

    /// <summary>The traditional fixed silence timeout elapsed. The unconditional
    /// ceiling: always commits, regardless of detector state.</summary>
    SilenceTimeout,

    /// <summary>The turn detector's P(end-of-turn) crossed the language
    /// threshold AND enough silence has passed to avoid clipping — committed
    /// early to cut latency.</summary>
    TurnDetectorEarly,
}

/// <summary>
/// How sure the commit is. <see cref="Tentative"/> commits get a short hold-back
/// grace window (the user may be mid-thought); <see cref="Confident"/> commits
/// dispatch immediately. Only meaningful when <c>Commit</c> is true.
/// </summary>
public enum CommitConfidence
{
    /// <summary>High-confidence end-of-turn — dispatch immediately, no grace.</summary>
    Confident,

    /// <summary>Soft-timeout commit with no positive turn-detector endorsement —
    /// hold a short grace window so a resumed thought is re-absorbed.</summary>
    Tentative,
}

/// <summary>Outcome of a single commit-decision call.</summary>
public readonly record struct EndOfTurnDecisionResult(
    bool Commit,
    CommitReason Reason,
    CommitConfidence Confidence = CommitConfidence.Confident);

/// <summary>
/// Decides whether the voice handler should commit the accumulated utterance
/// right now, based on silence duration and an optional turn-detector signal.
/// Pure policy — extracted from <see cref="DiscordVoiceHandler"/> so it's
/// unit-testable independent of Discord / audio / STT.
/// </summary>
internal static class EndOfTurnDecision
{
    /// <summary>
    /// Minimum silence required before an early "detector says END" commit.
    /// Raised from 200ms to 500ms on 2026-05-12 to avoid fragmenting multi-sentence
    /// thoughts on brief breath pauses; tuned to 300ms on 2026-05-13 alongside the
    /// "snappier" profile. Trade-off: a confident detector may now commit on an
    /// inter-sentence breath if it judges the first sentence already complete.
    /// See <c>docs/backlog/voice-end-of-turn-detection.md</c> for the rationale.
    /// </summary>
    public const int MinEarlyCommitSilenceMs = 300;

    /// <summary>
    /// Should the voice handler commit the current utterance — and if so, why?
    /// </summary>
    /// <param name="silenceElapsedMs">Time since the last detected speech frame.</param>
    /// <param name="silenceTimeoutMs">Base (soft) silence timeout. At this duration we
    /// normally commit; if the turn detector is below <paramref name="lowConfidenceThreshold"/>
    /// we extend up to <paramref name="maxSilenceTimeoutMs"/>.</param>
    /// <param name="useTurnDetector">When false, only the base timeout is honoured.</param>
    /// <param name="pEou">Most-recent P(end-of-turn) from the turn detector, or 0.</param>
    /// <param name="threshold">Language-specific high-confidence threshold.</param>
    /// <param name="lowConfidenceThreshold">Below this pEou the model is treated as
    /// actively saying "not finished" and the wait is extended.</param>
    /// <param name="maxSilenceTimeoutMs">Hard ceiling — commits regardless of detector
    /// state so a quiet user never strands the agent.</param>
    public static EndOfTurnDecisionResult Decide(
        int silenceElapsedMs,
        int silenceTimeoutMs,
        bool useTurnDetector,
        float pEou,
        float threshold,
        float lowConfidenceThreshold,
        int maxSilenceTimeoutMs)
    {
        // Hard ceiling always wins: a quiet user must never hang. We already
        // waited the maximum — treat as confident, never hold further.
        if (silenceElapsedMs >= maxSilenceTimeoutMs)
        {
            return new EndOfTurnDecisionResult(
                true, CommitReason.SilenceTimeout, CommitConfidence.Confident);
        }

        // Base timeout reached: normally commit, but extend if the model says
        // "doesn't sound finished" and we still have headroom before the cap.
        if (silenceElapsedMs >= silenceTimeoutMs)
        {
            if (useTurnDetector
                && pEou > 0.0f
                && pEou < lowConfidenceThreshold)
            {
                return new EndOfTurnDecisionResult(false, CommitReason.NotYet);
            }

            // Confident only if the detector positively endorsed the ending.
            // No detector, or pEou == 0 (no usable signal) → tentative: the
            // soft timeout alone committed, so the user may be mid-thought.
            var confidence = (useTurnDetector && pEou >= lowConfidenceThreshold)
                ? CommitConfidence.Confident
                : CommitConfidence.Tentative;
            return new EndOfTurnDecisionResult(
                true, CommitReason.SilenceTimeout, confidence);
        }

        // Early-commit path: high-confidence finished, enough silence for prosody.
        if (useTurnDetector
            && silenceElapsedMs >= MinEarlyCommitSilenceMs
            && pEou > threshold)
        {
            return new EndOfTurnDecisionResult(
                true, CommitReason.TurnDetectorEarly, CommitConfidence.Confident);
        }

        return new EndOfTurnDecisionResult(false, CommitReason.NotYet);
    }
}
