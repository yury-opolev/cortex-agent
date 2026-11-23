using System.Text.Json;
using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Contracts.Llm;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>A candidate dictionary term for the owner to review (<c>dictionary.suggested.jsonl</c>, sealed).</summary>
public sealed record LexiconSuggestion(string Term, string Kind, int Count, string Source);

/// <summary>
/// Candidates for the owner dictionary; never applied automatically (the owner moves accepted lines into
/// <c>dictionary.jsonl</c>). Two sources: proper-noun heuristics over the corpus (capitalised words that are not
/// sentence-initial and never appear in lower case), and optionally the configured LLM provider (D5 allows cloud
/// models to process history; no other destination ever sees the text). Terms the dictionary already covers are skipped.
/// </summary>
public static partial class LexiconSuggester
{
    /// <summary>Most characters sent to the LLM per batch.</summary>
    public const int BatchChars = 8000;

    private const string Prompt = """
        You find personal named entities in chat messages so they can be pseudonymised. Return ONLY a JSON array of
        objects {"term": "...", "kind": "person|place|org|handle"} with every name of a real person, every private or
        small place (street, village, venue), every organisation and every user handle that appears in the text, written
        exactly as in the text (one entry per spelling). Leave out common words, product names, programming terms and
        famous public places. Return [] when there is none.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "person", "place", "org", "handle" };

    /// <summary>Proper-noun candidates seen at least <paramref name="minCount"/> times, most frequent first.</summary>
    public static IReadOnlyList<LexiconSuggestion> Capitalised(IEnumerable<string> texts, SanitiserLexicon lexicon, int minCount = 2)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(lexicon);
        var capitalised = new Dictionary<string, int>(StringComparer.Ordinal);
        var lower = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var normalized = TextSanitiser.Normalize(text);
            foreach (Match word in WordPattern().Matches(normalized))
            {
                var value = word.Value;
                if (char.IsLower(value[0]))
                {
                    lower.Add(value);
                }
                else if (!IsSentenceStart(normalized, word.Index))
                {
                    capitalised[value] = capitalised.GetValueOrDefault(value) + 1;
                }
            }
        }

        return capitalised
            .Where(c => c.Value >= minCount && !lower.Contains(c.Key.ToLowerInvariant()) && lexicon.Lookup(c.Key) is null && !c.Key.All(char.IsUpper))
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => new LexiconSuggestion(c.Key, "unknown", c.Value, "capitalised"))
            .ToList();
    }

    /// <summary>Entities the LLM finds in up to <paramref name="maxBatches"/> batches of <paramref name="texts"/>.</summary>
    public static async Task<IReadOnlyList<LexiconSuggestion>> FromLlmAsync(
        IEnumerable<string> texts, SanitiserLexicon lexicon, ILlmClient llm, string model, int maxBatches, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(llm);
        var found = new Dictionary<(string Term, string Kind), int>();
        foreach (var batch in Batches(texts).Take(maxBatches))
        {
            var result = await llm.CompleteAsync(
                new LlmCompletionRequest
                {
                    Model = model,
                    Messages = [new LlmMessage { Role = "system", Content = Prompt }, new LlmMessage { Role = "user", Content = batch }],
                    Temperature = 0,
                    MaxTokens = 2000,
                    RequestId = Guid.NewGuid().ToString("N"),
                    ConversationId = "eval-corpus-suggest",
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var entity in Parse(result.Success ? result.Content : null))
            {
                found[(entity.Term, entity.Kind)] = found.GetValueOrDefault((entity.Term, entity.Kind)) + 1;
            }
        }

        return found
            .Where(f => lexicon.Lookup(f.Key.Term) is null)
            .OrderByDescending(f => f.Value)
            .ThenBy(f => f.Key.Term, StringComparer.Ordinal)
            .Select(f => new LexiconSuggestion(f.Key.Term, f.Key.Kind, f.Value, "llm"))
            .ToList();
    }

    private static IEnumerable<string> Batches(IEnumerable<string> texts)
    {
        var batch = new System.Text.StringBuilder();
        foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            if (batch.Length > 0 && batch.Length + text.Length > BatchChars)
            {
                yield return batch.ToString();
                batch.Clear();
            }

            batch.AppendLine(text.Length > BatchChars ? text[..BatchChars] : text);
        }

        if (batch.Length > 0)
        {
            yield return batch.ToString();
        }
    }

    private static List<(string Term, string Kind)> Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<Entity>>(MemoryConsolidationService.StripToJson(content), Json) ?? [])
                .Where(e => e.Term is { Length: >= 2 } && e.Kind is not null && Kinds.Contains(e.Kind))
                .Select(e => (e.Term!.Trim(), e.Kind!))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsSentenceStart(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i]))
        {
            i--;
        }

        return i < 0 || text[i] is '.' or '!' or '?' or ':' or '"' or '\n';
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])\p{L}[\p{L}'\-]{2,}(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    private sealed record Entity(string? Term, string? Kind);
}
