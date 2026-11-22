namespace Cortex.Contained.Agent.Host.History;

/// <summary>Result ordering for <c>history_search</c>.</summary>
public enum HistoryOrder
{
    /// <summary>BM25 relevance; unquoted terms are OR-joined with a document-frequency cap.</summary>
    Relevance,

    /// <summary>Exhaustive, message-level, oldest first; all terms are required.</summary>
    Time,
}

/// <summary>One parsed query item: a word, a phrase, or either with a trailing prefix marker.</summary>
/// <param name="Tokens">Folded FTS tokens, in order.</param>
/// <param name="IsPrefix">True when the last token is a prefix (<c>stem*</c>).</param>
public sealed record QueryTerm(IReadOnlyList<string> Tokens, bool IsPrefix)
{
    /// <summary>True for a single exact token (the only kind subject to the df cap).</summary>
    public bool IsPlainWord => this.Tokens.Count == 1 && !this.IsPrefix;

    /// <summary>The FTS5 expression for this term; tokens are always quoted.</summary>
    public string ToFts() =>
        "\"" + string.Join(' ', this.Tokens).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" + (this.IsPrefix ? "*" : string.Empty);
}

/// <summary>A query ready for <c>MATCH</c>.</summary>
/// <param name="MatchExpression">The FTS5 expression.</param>
/// <param name="Terms">Terms kept in the expression.</param>
/// <param name="DroppedTerms">Plain words dropped by the document-frequency cap.</param>
public sealed record ParsedHistoryQuery(string MatchExpression, IReadOnlyList<QueryTerm> Terms, IReadOnlyList<QueryTerm> DroppedTerms)
{
    /// <summary>
    /// Which keyword table the expression targets: <see cref="HistoryKeywordVariant.Plain"/> and
    /// <see cref="HistoryKeywordVariant.Stemmed"/> use <c>segments_fts</c>, <see cref="HistoryKeywordVariant.Trigram"/> uses <c>segments_tri</c>.
    /// </summary>
    public HistoryKeywordVariant Table { get; init; } = HistoryKeywordVariant.Plain;
}

/// <summary>
/// Parses agent queries (§9.3): quoted phrases stay phrases, <c>term*</c> is a prefix
/// (served by <c>prefix='3 4'</c>), everything else is folded, tokenized and quoted.
/// </summary>
public static class QueryParser
{
    /// <summary>Parser version.</summary>
    public const int Version = 1;

    /// <summary>Shorter stems are searched as exact words instead of prefixes.</summary>
    public const int MinPrefixLength = 3;

    /// <summary>The document-frequency cap only applies once the index has this many segments.</summary>
    public const int MinDocumentsForDfCap = 50;

    /// <summary>Splits <paramref name="raw"/> into folded, de-duplicated terms.</summary>
    public static IReadOnlyList<QueryTerm> Split(string? raw)
    {
        var terms = new List<QueryTerm>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return terms;
        }

        var position = 0;
        while (position < raw.Length)
        {
            if (char.IsWhiteSpace(raw[position]))
            {
                position++;
                continue;
            }

            if (raw[position] == '"')
            {
                var close = raw.IndexOf('"', position + 1);
                if (close > position)
                {
                    var prefix = close + 1 < raw.Length && raw[close + 1] == '*';
                    AddTerm(terms, raw[(position + 1)..close], prefix);
                    position = close + (prefix ? 2 : 1);
                    continue;
                }

                position++;
                continue;
            }

            var end = position;
            while (end < raw.Length && !char.IsWhiteSpace(raw[end]) && raw[end] != '"')
            {
                end++;
            }

            var chunk = raw[position..end];
            var isPrefix = chunk.EndsWith('*');
            AddTerm(terms, chunk.TrimEnd('*'), isPrefix);
            position = end;
        }

        return terms;
    }

    /// <summary>
    /// Builds the match expression. <see cref="HistoryOrder.Relevance"/> OR-joins terms and drops
    /// plain words whose document frequency exceeds <paramref name="dfCapRatio"/> (only once the index
    /// has <see cref="MinDocumentsForDfCap"/> segments). If no kept term occurs anywhere, the rarest
    /// dropped term is kept. <see cref="HistoryOrder.Time"/> requires every term (AND).
    /// Returns null when there is nothing to search for.
    /// </summary>
    public static ParsedHistoryQuery? Build(
        IReadOnlyList<QueryTerm> terms,
        HistoryOrder order,
        Func<string, long> documentFrequency,
        long totalDocuments,
        double dfCapRatio)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(documentFrequency);
        if (terms.Count == 0)
        {
            return null;
        }

        if (order == HistoryOrder.Time)
        {
            return new ParsedHistoryQuery(string.Join(" AND ", terms.Select(t => t.ToFts())), terms, []);
        }

        var kept = new List<QueryTerm>();
        var dropped = new List<(QueryTerm Term, long Df)>();
        var keptMatchable = false;
        foreach (var term in terms)
        {
            if (!term.IsPlainWord)
            {
                kept.Add(term);
                keptMatchable = true;
                continue;
            }

            var df = documentFrequency(term.Tokens[0]);
            if (totalDocuments >= MinDocumentsForDfCap && (double)df / totalDocuments > dfCapRatio)
            {
                dropped.Add((term, df));
                continue;
            }

            kept.Add(term);
            keptMatchable |= df > 0;
        }

        if (!keptMatchable && dropped.Count > 0)
        {
            var rarest = dropped.OrderBy(d => d.Df).First();
            kept.Add(rarest.Term);
            dropped.Remove(rarest);
        }

        return new ParsedHistoryQuery(
            string.Join(" OR ", kept.Select(t => t.ToFts())),
            kept,
            dropped.Select(d => d.Term).ToList());
    }

    /// <summary>
    /// Adapts a parsed query to the configured keyword variant (§4.2 ablation). Plain is unchanged.
    /// Stemmed keeps the exact expression on the <c>body</c> column and, for relevance order, adds the
    /// Snowball stems of every plain word on the <c>stem</c> column. Trigram relevance queries match
    /// each term of at least three characters as a substring in the trigram table. Time order always
    /// keeps exact word semantics on <c>body</c>.
    /// </summary>
    public static ParsedHistoryQuery ForVariant(ParsedHistoryQuery parsed, HistoryKeywordVariant variant, HistoryOrder order)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        switch (variant)
        {
            case HistoryKeywordVariant.Stemmed:
                var body = "body : (" + parsed.MatchExpression + ")";
                var stems = parsed.Terms.Where(t => t.IsPlainWord)
                    .SelectMany(t => HistoryStemmer.Stems(t.Tokens[0]))
                    .Distinct(StringComparer.Ordinal)
                    .Select(s => new QueryTerm([s], false).ToFts())
                    .ToList();
                var expression = order == HistoryOrder.Relevance && stems.Count > 0
                    ? body + " OR stem : (" + string.Join(" OR ", stems) + ")"
                    : body;
                return parsed with { MatchExpression = expression, Table = HistoryKeywordVariant.Stemmed };
            case HistoryKeywordVariant.Trigram when order == HistoryOrder.Relevance:
                var substrings = parsed.Terms
                    .Select(t => string.Join(' ', t.Tokens))
                    .Where(s => s.Length >= 3)
                    .Select(s => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")
                    .ToList();
                return substrings.Count == 0
                    ? parsed
                    : parsed with { MatchExpression = string.Join(" OR ", substrings), Table = HistoryKeywordVariant.Trigram };
            default:
                return parsed;
        }
    }

    private static void AddTerm(List<QueryTerm> terms, string text, bool isPrefix)
    {
        var tokens = HistoryTokenizer.Tokenize(TextFolder.Fold(text));
        if (tokens.Count == 0)
        {
            return;
        }

        var prefix = isPrefix && tokens[^1].Length >= MinPrefixLength;
        var term = new QueryTerm(tokens, prefix);
        if (!terms.Any(t => t.IsPrefix == term.IsPrefix && t.Tokens.SequenceEqual(term.Tokens, StringComparer.Ordinal)))
        {
            terms.Add(term);
        }
    }
}
