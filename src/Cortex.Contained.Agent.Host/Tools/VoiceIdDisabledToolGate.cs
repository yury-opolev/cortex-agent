using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.SpeakerId;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>Hides the voice-enrollment tool family while voice-id is disabled
/// (<see cref="SpeakerIdSettingsStore.IsVoiceIdEnabled"/> is false).</summary>
public sealed class VoiceIdDisabledToolGate : IConversationToolGate
{
    private static readonly FrozenSet<string> enrollmentToolNames = FrozenSet.ToFrozenSet(
        [
            "start_voice_enrollment",
            "decline_voice_enrollment",
            "cancel_voice_enrollment",
            "request_voice_reenrollment",
            "confirm_voice_reenrollment",
            "forget_voice_enrollment",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly SpeakerIdSettingsStore store;

    public VoiceIdDisabledToolGate(SpeakerIdSettingsStore store)
    {
        this.store = store;
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetHiddenTools(string? conversationId)
        => this.store.IsVoiceIdEnabled ? FrozenSet<string>.Empty : enrollmentToolNames;
}
