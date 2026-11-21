namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>Outcome of the paraphrase guard for one query (numbers only, never tokens: D6).</summary>
/// <param name="QueryId">Query id.</param>
/// <param name="Jaccard">Jaccard similarity of the folded token sets of query and gold messages.</param>
/// <param name="SharedRareTokens">Rare tokens (low document frequency) shared by query and gold messages.</param>
/// <param name="Passed">True when the query is a genuine paraphrase.</param>
public sealed record ParaphraseCheck(string QueryId, double Jaccard, int SharedRareTokens, bool Passed);

/// <summary>
/// Paraphrase guard (§4.2): a paraphrase query must not be answerable by word overlap — Jaccard of
/// the folded token sets ≤ 0.2 and no shared rare token. Runs locally on raw data; reports numbers only.
/// </summary>
public static class ParaphraseGuard
{
    /// <summary>Default maximum Jaccard similarity.</summary>
    public const double MaxJaccard = 0.2;

    /// <summary>A token is rare when it occurs in at most this share of indexed segments (and at most 3).</summary>
    public const double RareDocumentRatio = 0.01;

    /// <summary>Checks <paramref name="queryText"/> against the texts of its gold messages.</summary>
    public static ParaphraseCheck Check(
        string queryId, string queryText, IEnumerable<string> goldTexts, Func<string, long> documentFrequency, long totalDocuments)
    {
        ArgumentNullException.ThrowIfNull(goldTexts);
        ArgumentNullException.ThrowIfNull(documentFrequency);
        var query = Tokens(queryText);
        var gold = goldTexts.SelectMany(Tokens).ToHashSet(StringComparer.Ordinal);
        var union = query.Union(gold).Count();
        var shared = query.Intersect(gold).ToList();
        var jaccard = union == 0 ? 0 : (double)shared.Count / union;
        var rareLimit = Math.Max(3, (long)Math.Floor(totalDocuments * RareDocumentRatio));
        var sharedRare = shared.Count(t => documentFrequency(t) <= rareLimit);
        return new ParaphraseCheck(queryId, jaccard, sharedRare, jaccard <= MaxJaccard && sharedRare == 0);
    }

    private static HashSet<string> Tokens(string text) =>
        HistoryTokenizer.Tokenize(TextFolder.Fold(text)).ToHashSet(StringComparer.Ordinal);
}
