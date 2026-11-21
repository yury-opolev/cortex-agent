using System.Globalization;
using System.Text;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// Splits folded text into the tokens the FTS5 <c>unicode61</c> tokenizer produces: maximal
/// runs of letters (L*), numbers (N*) and private-use characters (Co). Used for segment word
/// counts and for query parsing, so both agree with the index.
/// </summary>
public static class HistoryTokenizer
{
    /// <summary>Returns the tokens of <paramref name="folded"/> in order.</summary>
    public static IReadOnlyList<string> Tokenize(string folded)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(folded))
        {
            return tokens;
        }

        var current = new StringBuilder();
        foreach (var rune in folded.EnumerateRunes())
        {
            if (IsTokenRune(rune))
            {
                current.Append(rune.ToString());
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Counts the tokens of <paramref name="folded"/> without allocating them.</summary>
    public static int CountTokens(string folded)
    {
        if (string.IsNullOrEmpty(folded))
        {
            return 0;
        }

        var count = 0;
        var inToken = false;
        foreach (var rune in folded.EnumerateRunes())
        {
            var isToken = IsTokenRune(rune);
            if (isToken && !inToken)
            {
                count++;
            }

            inToken = isToken;
        }

        return count;
    }

    private static bool IsTokenRune(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
        UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        UnicodeCategory.PrivateUse => true,
        _ => false,
    };
}
