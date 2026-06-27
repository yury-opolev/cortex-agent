namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Accumulates streaming text chunks and extracts complete sentences.
/// Used by the voice handler to split LLM output into sentences for
/// sentence-by-sentence TTS synthesis.
/// </summary>
/// <remarks>
/// Sentence boundaries are detected at '.', '!', '?' followed by whitespace
/// or end-of-buffer, with special handling for abbreviations, ellipsis,
/// decimal numbers, and quoted speech.
/// </remarks>
internal sealed class SentenceAccumulator
{
    /// <summary>Minimum characters before we consider a sentence boundary valid.</summary>
    private const int MinSentenceLength = 8;

    /// <summary>
    /// Common abbreviations that end with a period but are NOT sentence boundaries.
    /// All lowercase for case-insensitive comparison.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr",
        "st", "ave", "blvd", "rd",
        "inc", "ltd", "co", "corp", "dept",
        "vs", "etc", "approx", "est",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
        "e.g", "i.e", "a.m", "p.m",
    };

    private readonly List<string> completeSentences = [];
    private readonly System.Text.StringBuilder buffer = new();

    /// <summary>
    /// Append a text chunk from the LLM stream. After appending, check
    /// <see cref="TryGetNextSentence"/> for complete sentences.
    /// </summary>
    public void Append(string chunk)
    {
        this.buffer.Append(chunk);
        ExtractSentences();
    }

    /// <summary>
    /// Try to dequeue the next complete sentence.
    /// Returns <c>true</c> if a sentence was available, <c>false</c> otherwise.
    /// </summary>
    public bool TryGetNextSentence(out string sentence)
    {
        if (this.completeSentences.Count > 0)
        {
            sentence = this.completeSentences[0];
            this.completeSentences.RemoveAt(0);
            return true;
        }

        sentence = "";
        return false;
    }

    /// <summary>
    /// Flush any remaining text as a final sentence (for end-of-stream).
    /// Returns the remaining text, or null if the buffer is empty/whitespace.
    /// </summary>
    public string? Flush()
    {
        // Drain any already-extracted sentences first — they'll be dequeued via TryGetNextSentence.
        // This method returns only the residual partial sentence that hasn't been extracted yet.
        var remaining = this.buffer.ToString().Trim();
        this.buffer.Clear();

        return remaining.Length > 0 ? remaining : null;
    }

    /// <summary>Whether there are complete sentences ready to dequeue.</summary>
    public bool HasSentences => this.completeSentences.Count > 0;

    /// <summary>Whether the internal buffer has any text (partial or complete).</summary>
    public bool HasPending => this.buffer.Length > 0;

    /// <summary>Reset the accumulator, discarding all buffered text and sentences.</summary>
    public void Reset()
    {
        this.buffer.Clear();
        this.completeSentences.Clear();
    }

    // ── Private helpers ──────────────────────────────────────────────

    private void ExtractSentences()
    {
        var text = this.buffer.ToString();
        var startIdx = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            // Only consider sentence-ending punctuation
            if (ch is not ('.' or '!' or '?'))
            {
                continue;
            }

            // Must have enough content to be a real sentence
            var candidateLength = i - startIdx + 1;
            if (candidateLength < MinSentenceLength)
            {
                continue;
            }

            // Ellipsis: "..." — not a sentence boundary unless followed by uppercase or end
            if (ch == '.' && IsEllipsis(text, i))
            {
                // Skip to end of ellipsis
                while (i + 1 < text.Length && text[i + 1] == '.')
                {
                    i++;
                }
                continue;
            }

            // Decimal number: "3.14" — not a sentence boundary
            if (ch == '.' && IsDecimalPoint(text, i))
            {
                continue;
            }

            // Abbreviation: "Dr." — not a sentence boundary
            if (ch == '.' && IsAbbreviation(text, i, startIdx))
            {
                continue;
            }

            // Must be followed by whitespace, end-of-text, or closing quote+whitespace
            // to confirm it's a sentence boundary
            var nextIdx = i + 1;

            // Skip closing quotes/parentheses that follow the punctuation
            while (nextIdx < text.Length && text[nextIdx] is '"' or '\'' or ')' or '\u201D' or '\u2019')
            {
                nextIdx++;
            }

            // If we're at end of text, this is NOT a confirmed boundary yet —
            // the LLM might continue the sentence. Wait for more text.
            if (nextIdx >= text.Length)
            {
                continue;
            }

            // Must be followed by whitespace (space, newline, tab)
            if (!char.IsWhiteSpace(text[nextIdx]))
            {
                continue;
            }

            // We have a confirmed sentence boundary at nextIdx
            var sentence = text[startIdx..nextIdx].Trim();
            if (sentence.Length > 0)
            {
                this.completeSentences.Add(sentence);
            }
            startIdx = nextIdx;
        }

        // Keep only the unprocessed remainder in the buffer
        if (startIdx > 0)
        {
            this.buffer.Clear();
            this.buffer.Append(text[startIdx..]);
        }
    }

    /// <summary>Check if the period at position i is part of an ellipsis ("...").</summary>
    private static bool IsEllipsis(string text, int i)
    {
        // "..." — three or more consecutive dots
        return (i + 1 < text.Length && text[i + 1] == '.')
            || (i > 0 && text[i - 1] == '.');
    }

    /// <summary>Check if the period at position i is a decimal point (digit.digit).</summary>
    private static bool IsDecimalPoint(string text, int i)
    {
        return i > 0 && i + 1 < text.Length
            && char.IsDigit(text[i - 1])
            && char.IsDigit(text[i + 1]);
    }

    /// <summary>Check if the period at position i ends a known abbreviation.</summary>
    private static bool IsAbbreviation(string text, int i, int sentenceStart)
    {
        // Walk backwards from the period to find the word
        var wordEnd = i;
        var wordStart = i - 1;
        while (wordStart >= sentenceStart && char.IsLetter(text[wordStart]))
        {
            wordStart--;
        }
        wordStart++; // Move back to first letter

        if (wordStart >= wordEnd)
        {
            return false;
        }

        var word = text[wordStart..wordEnd];

        // Check compound abbreviations like "e.g" or "i.e" — walk further back past dots
        var extStart = wordStart;
        while (extStart > sentenceStart)
        {
            var prev = extStart - 1;
            if (prev >= sentenceStart && text[prev] == '.')
            {
                var prevWordStart = prev - 1;
                while (prevWordStart >= sentenceStart && char.IsLetter(text[prevWordStart]))
                {
                    prevWordStart--;
                }
                prevWordStart++;

                if (prevWordStart < prev)
                {
                    extStart = prevWordStart;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        if (extStart < wordStart)
        {
            var extendedWord = text[extStart..wordEnd];
            if (Abbreviations.Contains(extendedWord))
            {
                return true;
            }
        }

        return Abbreviations.Contains(word);
    }
}
