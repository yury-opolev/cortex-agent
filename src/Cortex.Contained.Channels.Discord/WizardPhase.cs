namespace Cortex.Contained.Channels.Discord;

/// <summary>Phase of a wizard enrollment capture session.</summary>
public enum WizardPhase
{
    /// <summary>Collecting the initial enrollment samples.</summary>
    Enrolling,
    /// <summary>Candidate voiceprint built; collecting confirmation matches.</summary>
    Confirming,
    /// <summary>Confirmed; the voiceprint is ready to submit.</summary>
    Complete,
}
