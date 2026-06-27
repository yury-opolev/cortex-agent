namespace Cortex.Contained.Speech.Tts;

/// <summary>Resolved provider + voice name for a specific language.</summary>
internal sealed record ResolvedVoice(ITtsProvider Provider, string VoiceName);
