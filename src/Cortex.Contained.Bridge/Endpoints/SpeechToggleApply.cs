using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Endpoints;

/// <summary>Applies optional speech-toggle updates to a <see cref="SpeechConfig"/>. Null = leave as-is.</summary>
public static class SpeechToggleApply
{
    public static void Apply(SpeechConfig speech, bool? speechEnabled, bool? sttEnabled, bool? ttsEnabled)
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
    }
}
