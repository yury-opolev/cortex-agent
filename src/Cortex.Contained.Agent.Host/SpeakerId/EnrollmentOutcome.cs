namespace Cortex.Contained.Agent.Host.SpeakerId;

using Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Outcome returned by <see cref="EnrollmentOrchestrator"/> transition methods.
/// </summary>
public abstract record EnrollmentOutcome
{
    private protected EnrollmentOutcome()
    {
    }

    /// <summary>The state transition succeeded.</summary>
    public sealed record Transitioned(
        VoiceEnrollmentState From,
        VoiceEnrollmentState To,
        string? Guidance = null) : EnrollmentOutcome;

    /// <summary>The transition was rejected because the current state doesn't permit it.</summary>
    public sealed record InvalidState(VoiceEnrollmentState Current, string Reason) : EnrollmentOutcome;

    /// <summary>Not enough captured samples are available to proceed.</summary>
    public sealed record InsufficientSamples(int Required, int Available) : EnrollmentOutcome;

    /// <summary>A confirmation utterance was checked.</summary>
    public sealed record Confirmation(
        ConfirmationResult Result,
        float Score,
        int MatchesAchieved,
        int MatchesRequired,
        int FailuresInARow,
        int FailuresAllowed,
        VoiceEnrollmentState NewState) : EnrollmentOutcome;

    /// <summary>Embedder or store failed; recoverable.</summary>
    public sealed record Errored(string Reason) : EnrollmentOutcome;
}

public enum ConfirmationResult
{
    Match,
    NoMatch,
    Inconclusive,
}
