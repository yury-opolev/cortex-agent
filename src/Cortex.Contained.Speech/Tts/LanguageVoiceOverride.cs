namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Per-language voice configuration with male/female voice references.
/// Voice references use "provider:voice" format (e.g. "silero-v5-russian:xenia").
/// </summary>
public sealed class LanguageVoiceConfig
{
    /// <summary>Male voice reference: "provider:voice".</summary>
    public required string MaleVoice { get; set; }

    /// <summary>Female voice reference: "provider:voice".</summary>
    public required string FemaleVoice { get; set; }

    /// <summary>
    /// Parses a "provider:voice" reference into its components.
    /// Falls back to (reference, reference) if no colon is present.
    /// </summary>
    public static (string ProviderName, string VoiceName) ParseVoiceReference(string reference)
    {
        var colonIndex = reference.IndexOf(':', System.StringComparison.Ordinal);
        return colonIndex > 0
            ? (reference[..colonIndex], reference[(colonIndex + 1)..])
            : (reference, reference);
    }

    /// <summary>Creates a voice reference string from provider name and voice name.</summary>
    public static string FormatVoiceReference(string providerName, string voiceName) =>
        $"{providerName}:{voiceName}";
}
