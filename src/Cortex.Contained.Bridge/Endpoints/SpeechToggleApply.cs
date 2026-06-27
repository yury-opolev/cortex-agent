using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Applies optional speech-toggle updates to a <see cref="SpeechConfig"/>. Null = leave as-is.</summary>
public static class SpeechToggleApply
{
    public static void Apply(SpeechConfig speech, bool? speechEnabled, bool? sttEnabled, bool? ttsEnabled, bool? voiceIdEnabled)
    {
        if (speechEnabled.HasValue)
        {
            speech.Enabled = speechEnabled.Value;
        }

        if (sttEnabled.HasValue)
        {
            speech.Stt.Enabled = sttEnabled.Value;
        }

        if (ttsEnabled.HasValue)
        {
            speech.Tts.Enabled = ttsEnabled.Value;
        }

        if (voiceIdEnabled.HasValue)
        {
            speech.VoiceId.Enabled = voiceIdEnabled.Value;
        }
    }

    /// <summary>
    /// The raw, persisted toggle state for the endpoint response. Sub-flags report stored
    /// intent — NOT the master-gated effective values — so the UI never writes a wiped
    /// sub-flag back and re-enabling the master switch cannot persist STT/TTS/voice-id as off.
    /// </summary>
    public static SpeechToggleState RawState(SpeechConfig speech)
        => new(speech.Enabled, speech.Stt.Enabled, speech.Tts.Enabled, speech.VoiceId.Enabled);

    /// <summary>
    /// Whether a Bridge restart is needed for a voice-id change to fully take effect. The desktop
    /// voice channel binds its speaker verifier at construction, so enabling voice-id at runtime
    /// needs a restart to attach it; disabling takes effect live (the sidecar stops). A failed
    /// live compose op also requires a restart.
    /// </summary>
    public static bool VoiceIdRestartRequired(bool effectiveBefore, bool effectiveAfter, bool composeConfirmed)
        => !composeConfirmed || (effectiveAfter && !effectiveBefore);
}

/// <summary>Raw (stored) speech-toggle flags returned to the settings UI.</summary>
public readonly record struct SpeechToggleState(bool SpeechEnabled, bool SttEnabled, bool TtsEnabled, bool VoiceIdEnabled);
