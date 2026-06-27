namespace Cortex.Contained.Speech.Tts;

/// <summary>Coarse Unicode script classes used for deterministic TTS language routing.</summary>
public enum TextScript
{
    /// <summary>No alphabetic characters (digits/punctuation/whitespace only).</summary>
    None,
    Latin,
    Cyrillic,
    Han,
    Kana,
    Hangul,
    Other,
}
