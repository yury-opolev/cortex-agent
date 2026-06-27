namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// The channel wizard-lock routing rule: when a wizard is active, committed
/// utterances feed enrollment capture only (the agent is bypassed).
/// </summary>
public static class WizardLockDecision
{
    /// <summary>Decide where to route a committed utterance.</summary>
    /// <param name="wizardActive">Whether the enrollment wizard currently owns the channel.</param>
    /// <returns>The route the committed utterance should take.</returns>
    public static UtteranceRoute Route(bool wizardActive)
        => wizardActive ? UtteranceRoute.EnrollmentCapture : UtteranceRoute.NormalDispatch;
}
