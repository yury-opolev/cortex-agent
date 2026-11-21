using MemoryMcp.Core.Retrieval;

namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>
/// Builds the text the reranker sees for one candidate (§4.3): the raw best-matching lines only — no
/// labels, timestamps, ids or summaries. Lines sharing the most folded query words are kept first,
/// within <c>maxChars</c>, and emitted in their original order. Deterministic.
/// </summary>
public static class RerankDocument
{
    /// <summary>The candidate document for <paramref name="query"/> from <paramref name="lines"/>.</summary>
    public static string Build(string query, IReadOnlyList<string> lines, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var queryTerms = KeywordTokenizer.Tokenize(TextFolder.Fold(query)).ToHashSet(StringComparer.Ordinal);
        var candidates = lines
            .Select((line, index) => (Index: index, Text: line.ReplaceLineEndings(" ").Trim()))
            .Where(l => l.Text.Length > 0)
            .Select(l => (l.Index, l.Text, Overlap: KeywordTokenizer.Tokenize(TextFolder.Fold(l.Text)).Distinct(StringComparer.Ordinal).Count(queryTerms.Contains)))
            .OrderByDescending(l => l.Overlap)
            .ThenBy(l => l.Index)
            .ToList();

        var chosen = new List<(int Index, string Text)>();
        var used = 0;
        foreach (var (index, text, _) in candidates)
        {
            var separator = chosen.Count == 0 ? 0 : 1;
            if (used + separator + text.Length <= maxChars)
            {
                chosen.Add((index, text));
                used += separator + text.Length;
            }
            else if (chosen.Count == 0)
            {
                chosen.Add((index, text[..maxChars]));
                used = maxChars;
            }
        }

        return string.Join('\n', chosen.OrderBy(c => c.Index).Select(c => c.Text));
    }
}
