namespace Cortex.Contained.Speech.Tts;

/// <summary>Language detector for routing TTS to per-language voices.</summary>
public interface ILanguageDetector
{
    /// <summary>ISO 639-1 code returned when the input is empty or no candidate has any signal.</summary>
    string Fallback { get; }

    /// <summary>Returns the best-guess ISO 639-1 code from the configured candidate set.</summary>
    string DetectTop(string text);

    /// <summary>Returns relative confidence per ISO 639-1 code; values sum to approximately 1 across configured candidates.</summary>
    IReadOnlyDictionary<string, double> DetectConfidences(string text);
}
