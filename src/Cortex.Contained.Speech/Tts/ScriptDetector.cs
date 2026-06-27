using System.Text;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Deterministic, length-independent script detection. Script is the reliable
/// signal for cross-script language routing (Cyrillic → Russian) — unlike
/// statistical n-gram detection it is unambiguous even for a single character.
/// </summary>
public static class ScriptDetector
{
    /// <summary>
    /// Returns the dominant alphabetic script in <paramref name="text"/>, or
    /// <see cref="TextScript.None"/> when the text contains no letters.
    /// </summary>
    public static TextScript DetectDominantScript(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return TextScript.None;
        }

        // One slot per TextScript value; indexing by (int)script gives a fixed,
        // input-order-independent tie-break (lowest enum value wins on a tie).
        Span<int> counts = stackalloc int[Enum.GetValues<TextScript>().Length];
        var hasKana = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            var script = Classify(rune.Value);
            counts[(int)script]++;
            if (script == TextScript.Kana)
            {
                hasKana = true;
            }
        }

        // Any kana is a strong Japanese signal even when kanji (Han) outnumber it,
        // so treat the text as Kana-script when kana is present at all.
        if (hasKana)
        {
            return TextScript.Kana;
        }

        var dominant = TextScript.None;
        var best = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > best)
            {
                best = counts[i];
                dominant = (TextScript)i;
            }
        }

        return dominant;
    }

    /// <summary>Maps an ISO 639-1 language code to the script it is written in.</summary>
    public static TextScript ScriptOf(string isoCode) => isoCode switch
    {
        "ru" or "uk" or "kk" => TextScript.Cyrillic,
        "zh" => TextScript.Han,
        "ja" => TextScript.Kana,
        "ko" => TextScript.Hangul,
        "en" or "da" or "de" or "fr" or "es" or "it" or "nl" or "pt" or "sv" or "no" or "fi" or "pl"
            => TextScript.Latin,
        _ => TextScript.Other,
    };

    private static TextScript Classify(int codePoint)
    {
        // Cyrillic + Cyrillic Supplement + extensions.
        if ((codePoint >= 0x0400 && codePoint <= 0x052F) || (codePoint >= 0x2DE0 && codePoint <= 0x2DFF) || (codePoint >= 0xA640 && codePoint <= 0xA69F))
        {
            return TextScript.Cyrillic;
        }

        // Basic Latin + Latin-1 Supplement + Latin Extended-A/B + Latin Extended Additional.
        if ((codePoint >= 0x0041 && codePoint <= 0x024F) || (codePoint >= 0x1E00 && codePoint <= 0x1EFF))
        {
            return TextScript.Latin;
        }

        // CJK Unified Ideographs.
        if (codePoint >= 0x4E00 && codePoint <= 0x9FFF)
        {
            return TextScript.Han;
        }

        // Hiragana + Katakana.
        if (codePoint >= 0x3040 && codePoint <= 0x30FF)
        {
            return TextScript.Kana;
        }

        // Hangul syllables.
        if (codePoint >= 0xAC00 && codePoint <= 0xD7AF)
        {
            return TextScript.Hangul;
        }

        return TextScript.Other;
    }
}
