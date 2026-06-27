namespace Cortex.Contained.Channels.Discord;

/// <summary>What the receive loop should do with a commit decision.</summary>
internal enum HoldBackOutcome
{
    /// <summary>Dispatch the accumulated utterance to STT + agent now.</summary>
    DispatchNow,

    /// <summary>Do not dispatch; keep accumulating within the grace window.</summary>
    Hold,

    /// <summary>The hard utterance-length cap was hit — dispatch unconditionally.</summary>
    ForceDispatch,
}

/// <summary>
/// Decides whether a committed utterance should dispatch immediately, hold a
/// short grace window (so a resumed mid-thought is re-absorbed into the same
/// turn), or force-dispatch because the absolute length cap was reached.
/// Pure policy — unit-testable without Discord / audio / STT. The receive loop
/// handles the speech-resumed case itself (sets <c>GraceUsed</c>, keeps
/// accumulating) before calling this, so the policy never sees mid-grace speech.
/// </summary>
internal static class HoldBackDecision
{
    /// <param name="confidence">Confidence of the underlying end-of-turn commit.</param>
    /// <param name="graceUsed">True once this turn already consumed its single
    /// grace window (a resumed thought was re-absorbed) — no second hold.</param>
    /// <param name="accumulatedMs">Total accumulated audio for this turn (ms).</param>
    /// <param name="maxUtteranceMs">Absolute ceiling on a single turn's audio.</param>
    /// <param name="graceElapsedMs">Time since the grace window opened (ms); 0 on
    /// the first decision for this commit.</param>
    /// <param name="graceWindowMs">Grace window duration (ms). 0 disables hold-back.</param>
    public static HoldBackOutcome Decide(
        CommitConfidence confidence,
        bool graceUsed,
        int accumulatedMs,
        int maxUtteranceMs,
        int graceElapsedMs,
        int graceWindowMs)
    {
        // Absolute ceiling always wins — never exceed max utterance duration.
        if (accumulatedMs >= maxUtteranceMs)
        {
            return HoldBackOutcome.ForceDispatch;
        }

        // Confident commit, or this turn already used its one grace window —
        // dispatch immediately (no added latency, no infinite re-absorb).
        if (confidence == CommitConfidence.Confident || graceUsed)
        {
            return HoldBackOutcome.DispatchNow;
        }

        // Tentative: still inside the grace window → keep holding.
        if (graceElapsedMs < graceWindowMs)
        {
            return HoldBackOutcome.Hold;
        }

        // Grace window expired with no resumed speech — dispatch.
        return HoldBackOutcome.DispatchNow;
    }
}
