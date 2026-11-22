namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// The tokenizer of the history index: <see cref="KeywordTokenizer"/> (the FTS5 <c>unicode61</c>
/// token rules), used for segment word counts and for query parsing, so both agree with the index.
/// </summary>
public static class HistoryTokenizer
{
    /// <summary>Returns the tokens of <paramref name="folded"/> in order.</summary>
    public static IReadOnlyList<string> Tokenize(string folded) => KeywordTokenizer.Tokenize(folded);

    /// <summary>Counts the tokens of <paramref name="folded"/> without allocating them.</summary>
    public static int CountTokens(string folded) => KeywordTokenizer.CountTokens(folded);
}
