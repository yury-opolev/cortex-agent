using Cortex.Contained.Contracts.Config;
using Cortex.Contained.Speech.SpeakerId;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>Chooses the speaker verifier for the voice channel: the real verifier when
/// voice-id is effectively enabled, otherwise null (verification skipped).</summary>
public static class VoiceIdVerifierSelector
{
    public static ISpeakerVerifier? Select(ISpeakerVerifier? verifier, SpeechConfig speech)
        => SpeechToggles.EffectiveVoiceId(speech) ? verifier : null;
}
