namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure policy mapping (enroll-started, voice-state) to the enrollment entry action:
/// start the spoken wizard now (user already in voice), ring then start on join, or
/// nothing (enroll didn't start). Unit-testable without Discord.
/// </summary>
public static class WizardEntryDecision
{
    /// <summary>Decide the entry action.</summary>
    public static WizardEntryAction Decide(bool enrollStarted, EnrollGateDecision gate)
        => !enrollStarted ? WizardEntryAction.None
            : gate == EnrollGateDecision.Proceed ? WizardEntryAction.StartNow
            : WizardEntryAction.RingThenStart;
}
