namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Per-tenant lifecycle of voice-identification enrollment.
/// </summary>
/// <remarks>
/// Integer values are stable and persisted by voiceprint stores — reordering
/// is a breaking schema change.
/// </remarks>
public enum VoiceEnrollmentState
{
    Unknown = 0,
    Declined = 1,
    Enrolling = 2,
    Confirming = 3,
    PendingReenroll = 4,
    Enrolled = 5,
}
