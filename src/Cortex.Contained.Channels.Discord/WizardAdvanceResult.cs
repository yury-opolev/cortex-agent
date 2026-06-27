namespace Cortex.Contained.Channels.Discord;

/// <summary>Outcome of advancing the wizard by one utterance.</summary>
public enum WizardAdvanceResult
{
    /// <summary>The wizard captured the utterance and is awaiting more.</summary>
    InProgress,

    /// <summary>The wizard completed: the voiceprint has been submitted.</summary>
    Completed,
}
