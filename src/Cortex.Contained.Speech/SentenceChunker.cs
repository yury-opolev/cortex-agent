using System.Text;

namespace Cortex.Contained.Speech;

/// <summary>
/// Splits text into sentence-sized chunks suitable for batch TTS synthesis.
/// Primary split: sentence boundaries (. ! ? …) with English abbreviation handling.
/// Fallback for overlong sentences: clause boundaries (, ; — –).
/// Last resort: word boundaries.
/// </summary>
public static class SentenceChunker
{
    /// <summary>Default ceiling — sentences below this go through unsplit.</summary>
    public const int DefaultMaxChunkChars = 600;

    private static readonly HashSet<string> abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Titles
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st",
        // Common
        "e.g", "i.e", "etc", "vs", "cf",
        // Org/place
        "inc", "ltd", "co", "corp", "u.s", "u.k", "e.u",
        // Time/date
        "a.m", "p.m", "no",
        // Units
        "kg", "lb", "oz", "ft", "in", "mi", "km", "m", "cm", "mm",
    };

    public static IReadOnlyList<string> Split(string text, int maxChunkChars = DefaultMaxChunkChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        text = NormalizeWhitespace(text);

        var sentences = SplitOnSentenceBoundaries(text);
        var result = new List<string>();

        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length <= maxChunkChars)
            {
                result.Add(EnsureTerminalPunctuation(trimmed));
            }
            else
            {
                var terminator = ExtractTerminator(trimmed);
                result.AddRange(SplitLongSentence(trimmed, maxChunkChars, terminator));
            }
        }

        return result;
    }

    private static List<string> SplitOnSentenceBoundaries(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            current.Append(ch);

            if (ch is not ('.' or '!' or '?'))
            {
                continue;
            }

            // Ellipsis (... or .. – treat as continuation, not boundary).
            // Skip if next char is a dot (start of ellipsis) or previous char is a dot (middle/end of ellipsis).
            if (ch == '.' && ((i + 1 < text.Length && text[i + 1] == '.') || (i > 0 && text[i - 1] == '.')))
            {
                continue;
            }

            // Abbreviation
            if (ch == '.' && IsAbbreviation(text, i))
            {
                continue;
            }

            // Dotted abbreviation mid-token (e.g. the dot between 'e' and 'g' in 'e.g.').
            // If the next non-space character after this dot is a lowercase letter, it's an intra-abbreviation dot.
            if (ch == '.' && i + 1 < text.Length && char.IsLower(text[i + 1]))
            {
                continue;
            }

            // End of input or followed by whitespace + new-sentence-start.
            if (i + 1 >= text.Length || IsFollowedByNewSentence(text, i + 1))
            {
                sentences.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            sentences.Add(current.ToString());
        }

        return sentences;
    }

    private static bool IsFollowedByNewSentence(string text, int startIndex)
    {
        var i = startIndex;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            return true;
        }

        var next = text[i];
        return char.IsLetter(next) || char.IsDigit(next) || next is '"' or '\'' or '(' or '[' or '—' or '-';
    }

    private static bool IsAbbreviation(string text, int dotIndex)
    {
        // Find the word immediately before the dot.
        var wordStart = dotIndex - 1;
        while (wordStart >= 0 && (char.IsLetter(text[wordStart]) || text[wordStart] == '.'))
        {
            wordStart--;
        }

        wordStart++;

        if (wordStart >= dotIndex)
        {
            return false;
        }

        var word = text[wordStart..dotIndex].TrimEnd('.');

        if (abbreviations.Contains(word))
        {
            return true;
        }

        // Single uppercase letter + dot → likely initial (e.g. "J. Smith")
        if (word.Length == 1 && char.IsUpper(word[0]))
        {
            return true;
        }

        return false;
    }

    private static List<string> SplitLongSentence(string sentence, int maxChunkChars, char terminator)
    {
        var chunks = new List<string>();
        var remaining = sentence.AsSpan();

        while (remaining.Length > maxChunkChars)
        {
            var splitIndex = FindSecondarySplitPoint(remaining, maxChunkChars);
            var isForceSplit = splitIndex <= 0;
            if (isForceSplit)
            {
                splitIndex = maxChunkChars;
                while (splitIndex > maxChunkChars / 2 && !char.IsWhiteSpace(remaining[splitIndex]))
                {
                    splitIndex--;
                }

                if (splitIndex <= maxChunkChars / 2)
                {
                    splitIndex = maxChunkChars;
                }
            }

            var chunk = remaining[..splitIndex].ToString().Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(EnsureContinuingPunctuation(chunk));
            }

            remaining = remaining[splitIndex..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            var last = remaining.ToString().Trim();
            if (last.Length > 0)
            {
                chunks.Add(ApplyTerminator(last, terminator));
            }
        }

        return chunks;
    }

    private static int FindSecondarySplitPoint(ReadOnlySpan<char> text, int maxChunkChars)
    {
        for (var i = Math.Min(maxChunkChars, text.Length) - 1; i > maxChunkChars / 3; i--)
        {
            if (text[i] is ',' or ';' or '—' or '–')
            {
                return i + 1;
            }
        }

        return -1;
    }

    private static string EnsureTerminalPunctuation(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var lastChar = text[^1];
        if (lastChar is '.' or '!' or '?' or '…')
        {
            return text;
        }

        if (lastChar is ',' or ';' or ':' or '—' or '–')
        {
            return text[..^1] + ".";
        }

        return text + ".";
    }

    private static char ExtractTerminator(string sentence)
    {
        if (sentence.Length == 0)
        {
            return '.';
        }

        var lastChar = sentence[^1];
        return lastChar switch
        {
            '.' or '!' or '?' or '…' => lastChar,
            _ => '.',
        };
    }

    private static string EnsureContinuingPunctuation(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var lastChar = text[^1];
        if (lastChar is ',' or ';' or '—' or '–')
        {
            return text;
        }

        if (lastChar is ':')
        {
            return text[..^1] + ",";
        }

        if (lastChar is '.' or '!' or '?' or '…')
        {
            return text[..^1] + ",";
        }

        return text + ",";
    }

    private static string ApplyTerminator(string text, char terminator)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var lastChar = text[^1];
        if (lastChar == terminator || lastChar is '…')
        {
            return text;
        }

        if (lastChar is '.' or '!' or '?' or ',' or ';' or ':' or '—' or '–')
        {
            return text[..^1] + terminator;
        }

        return text + terminator;
    }

    private static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var prevWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWasSpace)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }
}
