using System.Globalization;
using System.Text;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Deterministic text folding applied to indexed text and to queries alike:
/// NFKC → lower case → ø→o, æ→ae, å→a, ß→ss, ё→е → strip remaining diacritics, keeping й.
/// Versioned because it changes index content (<see cref="HistoryConfigFingerprint"/>).
/// </summary>
public static class TextFolder
{
    /// <summary>Folding version; bump on any behaviour change.</summary>
    public const int Version = 1;

    /// <summary>Folds <paramref name="text"/>; null or empty yields an empty string.</summary>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var lowered = ReplaceLoneSurrogates(text).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(lowered.Length);
        for (var i = 0; i < lowered.Length; i++)
        {
            var ch = lowered[i];
            if (char.IsHighSurrogate(ch) && i + 1 < lowered.Length)
            {
                builder.Append(ch).Append(lowered[i + 1]);
                i++;
                continue;
            }

            switch (ch)
            {
                case 'ø':
                    builder.Append('o');
                    continue;
                case 'æ':
                    builder.Append("ae");
                    continue;
                case 'å':
                    builder.Append('a');
                    continue;
                case 'ß':
                    builder.Append("ss");
                    continue;
                case 'ё':
                    builder.Append('е');
                    continue;
                case 'й':
                    builder.Append('й');
                    continue;
            }

            foreach (var part in ch.ToString().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(part) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(part);
                }
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string ReplaceLoneSurrogates(string text)
    {
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var valid = !char.IsSurrogate(ch)
                || (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
            if (char.IsHighSurrogate(ch) && valid)
            {
                builder?.Append(ch).Append(text[i + 1]);
                i++;
                continue;
            }

            if (!valid)
            {
                builder ??= new StringBuilder(text, 0, i, text.Length);
                builder.Append('\uFFFD');
                continue;
            }

            builder?.Append(ch);
        }

        return builder?.ToString() ?? text;
    }
}
