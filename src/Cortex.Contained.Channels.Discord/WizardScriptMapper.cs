namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Maps the Bridge's local wizard <see cref="WizardPhase"/> to the spoken line to
/// deliver next, by delegating to <see cref="EnrollmentScript"/>. The voice handler
/// calls this after each captured utterance to drive the "repeat-after-me" prompts.
/// </summary>
public static class WizardScriptMapper
{
    /// <summary>Select the line to speak for the given local phase and progress.</summary>
    public static EnrollmentLine LineFor(WizardPhase phase, int capturedInPhase, int samplesRequired, int matchesRequired)
        => phase switch
        {
            WizardPhase.Enrolling => EnrollmentScript.LineFor("Enrolling", capturedInPhase, samplesRequired),
            WizardPhase.Confirming => EnrollmentScript.LineFor("Confirming", capturedInPhase, matchesRequired),
            WizardPhase.Complete => EnrollmentScript.LineFor("Enrolled", 0, 0),
            _ => EnrollmentScript.LineFor("Unknown", 0, 0),
        };
}
