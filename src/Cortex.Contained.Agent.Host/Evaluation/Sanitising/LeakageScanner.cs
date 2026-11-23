using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.Security;
using MemoryMcp.Core.Retrieval;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>A residual personal value in sanitised output: where and what kind, never the text.</summary>
public sealed record LeakageFinding(string Location, string Kind, int Count);

/// <summary>
/// The fail-closed residual-leakage check of a sanitised corpus. Every sanitised text (messages, channel and user
/// ids, tool-call summaries, memories, tags, query datasets, plans) is normalised (NFKC, zero-width removed) and
/// scanned for: owner dictionary terms on the folded text (no case, no diacritics, so spelling variants the
/// sanitiser missed are caught; same word and inflection rules as the sanitiser, <c>_</c> and digits are boundaries); the personal-identifier patterns of <see cref="SensitiveDataRedactor"/>; digit
/// runs of 9 or more; <c>@handles</c>; URL hosts that are neither public nor pseudonyms. Produced pseudonyms are
/// exempt. A single finding fails the freeze: the version is deleted, the report lists locations and kinds only.
/// </summary>
public sealed partial class LeakageScanner
{
    private readonly HashSet<string> pseudonyms;
    private readonly IReadOnlySet<string> publicHosts;
    private readonly Regex? terms;
    private readonly Dictionary<(string Location, string Kind), int> findings = [];
    private readonly bool termsOnly;

    /// <param name="lexicon">The owner dictionary.</param>
    /// <param name="pseudonyms">Every pseudonym the sanitiser produced (exempt).</param>
    /// <param name="termsOnly">Only dictionary terms (re-checking a version after the dictionary grew; its pseudonyms are unknown then).</param>
    public LeakageScanner(SanitiserLexicon lexicon, IEnumerable<string> pseudonyms, bool termsOnly = false)
    {
        ArgumentNullException.ThrowIfNull(lexicon);
        ArgumentNullException.ThrowIfNull(pseudonyms);
        this.termsOnly = termsOnly;
        this.pseudonyms = pseudonyms.ToHashSet(StringComparer.Ordinal);
        this.publicHosts = lexicon.PublicHosts;
        var folded = lexicon.Entries.Where(e => e.Kind != "public-host").Select(e => SanitiserLexicon.Canonical(e.Term)).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        this.terms = folded.Count == 0 ? null : SanitiserLexicon.BuildMatcher(folded);
    }

    /// <summary>Texts scanned so far.</summary>
    public int Scanned { get; private set; }

    /// <summary>True while nothing was found.</summary>
    public bool Passed => this.findings.Count == 0;

    /// <summary>Findings by location and kind (counts only).</summary>
    public IReadOnlyList<LeakageFinding> Findings =>
        this.findings.Select(f => new LeakageFinding(f.Key.Location, f.Key.Kind, f.Value)).OrderBy(f => f.Location, StringComparer.Ordinal).ThenBy(f => f.Kind, StringComparer.Ordinal).ToList();

    /// <summary>Scans one sanitised text found at <paramref name="location"/> (e.g. <c>messages.Content</c>).</summary>
    public void Scan(string location, string? text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        this.Scanned++;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var normalized = TextSanitiser.Normalize(text);
        if (this.terms is not null)
        {
            var foldedText = TextFolder.Fold(normalized);
            this.Add(location, "dictionary-term", this.terms.Matches(foldedText).Count(m => !InsidePseudonymToken(foldedText, m)));
        }

        if (this.termsOnly)
        {
            return;
        }

        foreach (var match in SensitiveDataRedactor.FindPersonalIdentifiers(normalized))
        {
            var value = normalized.Substring(match.Index, match.Length);
            if (!this.IsPseudonym(value) && !this.InsidePseudonym(normalized, match.Index, match.Length))
            {
                this.Add(location, match.Kind);
            }
        }

        foreach (Match number in LongNumberPattern().Matches(normalized))
        {
            if (!this.pseudonyms.Contains(number.Value))
            {
                this.Add(location, "number");
            }
        }

        foreach (Match handle in HandlePattern().Matches(normalized))
        {
            if (!this.pseudonyms.Contains(handle.Groups["name"].Value))
            {
                this.Add(location, "handle");
            }
        }

        foreach (Match host in HostPattern().Matches(normalized))
        {
            var name = host.Groups["host"].Value.ToLowerInvariant();
            if (!this.publicHosts.Contains(name) && !this.pseudonyms.Contains(name))
            {
                this.Add(location, "url-host");
            }
        }
    }

    // The sanitiser's word rule applies to the folded text; a term can also sit between digits inside a pseudonym token (person….).
    private static bool InsidePseudonymToken(string text, Match match)
    {
        var start = match.Index;
        while (start > 0 && char.IsLetterOrDigit(text[start - 1]))
        {
            start--;
        }

        var end = match.Index + match.Length;
        while (end < text.Length && char.IsLetterOrDigit(text[end]))
        {
            end++;
        }

        return PseudonymMap.IsPseudonymToken(text.AsSpan(start, end - start));
    }

    private bool IsPseudonym(string value) => this.pseudonyms.Contains(value);

    // A pattern can match inside a pseudonym (e.g. the e-mail pattern inside mail…@example.invalid is the pseudonym itself).
    private bool InsidePseudonym(string text, int index, int length)
    {
        var start = index;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var end = index + length;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        var word = text[start..end].Trim('<', '>', '(', ')', '[', ']', ',', ';', '.', '"', '\'', ':');
        return this.pseudonyms.Contains(word);
    }

    private void Add(string location, string kind, int count = 1)
    {
        if (count > 0)
        {
            this.findings[(location, kind)] = this.findings.GetValueOrDefault((location, kind)) + count;
        }
    }

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{9,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumberPattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_@.])@(?<name>(?=[A-Za-z0-9_.]*[A-Za-z_])[A-Za-z0-9_.]{2,32})(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
}
