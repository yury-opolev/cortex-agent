using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Decides whether the <c>speech:</c> section must be written to cortex.yml.
/// Extracted from <c>PersistSettingsToYaml</c> so the rule is unit-testable: a
/// missed condition here silently drops config from disk. Specifically, the
/// per-language voice config (<c>speech.tts.languages</c>) was being lost on
/// save whenever STT/TTS engine settings were at their defaults, because the
/// guard didn't account for configured languages (regression fixed 2026-05-24).
/// </summary>
public static class SpeechYamlPolicy
{
    /// <summary>
    /// True when any non-default speech setting is present and therefore the
    /// whole <c>speech:</c> section must be persisted. Returns false only when
    /// every setting is at its default AND no per-language voices / non-default
    /// fallback language are configured.
    /// </summary>
    public static bool ShouldWriteSpeechSection(SpeechConfig speech)
    {
        ArgumentNullException.ThrowIfNull(speech);

        return speech.Stt.Engine != "whisper"
            || !string.IsNullOrWhiteSpace(speech.Stt.WhisperModelPath)
            || speech.Stt.Language != "en"
            || speech.Tts.Engine != "kokoro"
            || speech.Tts.KokoroVoice != "af_heart"
            || !string.IsNullOrWhiteSpace(speech.Tts.KokoroModelPath)
            || !string.IsNullOrWhiteSpace(speech.Tts.WindowsVoiceName)
            || speech.Tts.WindowsSpeechRate != 0
            || speech.Tts.Languages.Count > 0          // per-language voice config
            || speech.Tts.DefaultLanguage != "en";     // non-default fallback language
    }
}
