namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Computes effective speech-subsystem enablement: a subsystem is on only when the
/// master <see cref="SpeechConfig.Enabled"/> switch AND its own flag are both true.
/// Kept as a static helper (not computed properties) so the booleans never leak into
/// serialized YAML/JSON config.
/// </summary>
public static class SpeechToggles
{
    /// <summary>True when STT should run: master AND stt flag.</summary>
    public static bool EffectiveStt(SpeechConfig speech) => speech.Enabled && speech.Stt.Enabled;

    /// <summary>True when TTS should run: master AND tts flag.</summary>
    public static bool EffectiveTts(SpeechConfig speech) => speech.Enabled && speech.Tts.Enabled;

    /// <summary>True when voice-id should run: master AND voice-id flag.</summary>
    public static bool EffectiveVoiceId(SpeechConfig speech) => speech.Enabled && speech.VoiceId.Enabled;
}
