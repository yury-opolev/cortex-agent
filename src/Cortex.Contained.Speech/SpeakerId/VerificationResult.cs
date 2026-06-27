namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Result of <see cref="ISpeakerVerifier.VerifyAsync"/>. Each variant carries
/// whether the caller should pass the corresponding transcript through to the
/// agent (<see cref="PassesTranscript"/> is true for every variant except
/// <see cref="Reject"/>).
/// </summary>
public abstract record VerificationResult
{
    private protected VerificationResult()
    {
    }

    /// <summary>
    /// True when the channel should dispatch the corresponding transcript to
    /// the agent. False only for <see cref="Reject"/>.
    /// </summary>
    public abstract bool PassesTranscript { get; }

    /// <summary>Voiceprint matched above threshold.</summary>
    public sealed record Accept(float Score) : VerificationResult
    {
        public override bool PassesTranscript => true;
    }

    /// <summary>Voiceprint did not match — transcript is dropped.</summary>
    public sealed record Reject(float Score) : VerificationResult
    {
        public override bool PassesTranscript => false;
    }

    /// <summary>Tenant has no active enrollment — pass through.</summary>
    public sealed record NotEnrolledResult : VerificationResult
    {
        public override bool PassesTranscript => true;
    }

    /// <summary>Singleton instance for the not-enrolled case.</summary>
    public static readonly VerificationResult NotEnrolled = new NotEnrolledResult();

    /// <summary>Reasons the verifier skipped without producing a similarity score.</summary>
    public enum SkipReason
    {
        FeatureOff,
        EnrollmentInProgress,
        TooShort,
        Error,
    }

    /// <summary>Verification was bypassed; transcript passes through.</summary>
    public sealed record Skipped(SkipReason Reason) : VerificationResult
    {
        public override bool PassesTranscript => true;
    }
}
