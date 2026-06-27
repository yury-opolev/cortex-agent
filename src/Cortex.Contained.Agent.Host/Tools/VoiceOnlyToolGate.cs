using System.Collections.Frozen;
using Cortex.Contained.Agent.Host.SpeakerId;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Hides the voice-only tool family (delayed-speech + voice-enrollment tools) from any
/// conversation that is not a Discord voice session. Replaces the former hard-coded prefix
/// check inside <see cref="ToolRegistry"/>; voice visibility now lives behind the generic
/// <see cref="IConversationToolGate"/> extension point.
/// </summary>
public sealed class VoiceOnlyToolGate : IConversationToolGate
{
    private static readonly FrozenSet<string> voiceOnlyToolNames = FrozenSet.ToFrozenSet(
        [
            "speak_after_delay",
            "cancel_delayed_speech",
            "start_voice_enrollment",
            "decline_voice_enrollment",
            "cancel_voice_enrollment",
            "request_voice_reenrollment",
            "confirm_voice_reenrollment",
            "forget_voice_enrollment",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlySet<string> GetHiddenTools(string? conversationId)
    {
        var isVoice = !string.IsNullOrEmpty(conversationId)
            && conversationId.StartsWith(VoiceEnrollmentToolHelpers.VoiceConversationPrefix, StringComparison.Ordinal);
        return isVoice ? FrozenSet<string>.Empty : voiceOnlyToolNames;
    }
}
