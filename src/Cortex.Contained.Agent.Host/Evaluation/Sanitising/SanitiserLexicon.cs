using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MemoryMcp.Core.Retrieval;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>One owner-maintained dictionary line (<c>dictionary.jsonl</c>, sealed on an encrypted install).</summary>
public sealed record LexiconEntry
{
    /// <summary>The term as it appears in text (a name, a place, an organisation, a handle).</summary>
    public required string Term { get; init; }

    /// <summary>One of <see cref="SanitiserLexicon.Kinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Groups variants (nicknames, spellings, scripts) under one pseudonym; defaults to the term.</summary>
    public string? Entity { get; init; }
}

/// <summary>
/// The owner's dictionary of personal terms. Matching is case-insensitive, on whole words, with up to three
/// trailing letters for inflection (Danish genitive, Russian cases) for terms of four or more letters; the
/// folded form of every term (no diacritics) is matched as well. <c>public-host</c> entries are URL hosts kept
/// as they are (public sites); every other kind is pseudonymised.
/// </summary>
public sealed class SanitiserLexicon
{
    /// <summary>Known kinds.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["person", "place", "org", "handle", "other", "public-host"];

    private const int MinTermLength = 2;
    private const int InflectedTermLength = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, LexiconEntry> byCanonical;

    private SanitiserLexicon(IReadOnlyList<LexiconEntry> entries)
    {
        this.Entries = entries;
        this.PublicHosts = entries.Where(e => e.Kind == "public-host").Select(e => e.Term.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var names = entries.Where(e => e.Kind != "public-host").ToList();
        this.byCanonical = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
        foreach (var entry in names)
        {
            this.byCanonical.TryAdd(Canonical(entry.Term), entry);
        }

        var variants = names.SelectMany(e => new[] { e.Term, TextFolder.Fold(e.Term) }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        this.Matcher = variants.Count == 0 ? null : Build(variants);
    }

    /// <summary>All entries.</summary>
    public IReadOnlyList<LexiconEntry> Entries { get; }

    /// <summary>URL hosts kept as they are (lower case).</summary>
    public IReadOnlySet<string> PublicHosts { get; }

    /// <summary>Whole-word matcher of every term (group <c>term</c>, inflection in group <c>suffix</c>), or null when empty.</summary>
    public Regex? Matcher { get; }

    /// <summary>An empty dictionary (patterns only).</summary>
    public static SanitiserLexicon Empty { get; } = new([]);

    /// <summary>The canonical form a term and its matches share: folded (NFKC, lower case, no diacritics), trimmed.</summary>
    public static string Canonical(string term) => TextFolder.Fold(term).Trim();

    /// <summary>Parses <c>dictionary.jsonl</c>; throws <see cref="InvalidOperationException"/> naming the bad line numbers.</summary>
    public static SanitiserLexicon Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var entries = new List<LexiconEntry>();
        var bad = new List<int>();
        var number = 0;
        foreach (var line in lines)
        {
            number++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LexiconEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LexiconEntry>(line, Json);
            }
            catch (JsonException)
            {
                entry = null;
            }

            if (entry is null || entry.Term.Trim().Length < MinTermLength || !Kinds.Contains(entry.Kind, StringComparer.Ordinal))
            {
                bad.Add(number);
                continue;
            }

            entries.Add(entry with { Term = entry.Term.Trim() });
        }

        return bad.Count == 0
            ? new SanitiserLexicon(entries)
            : throw new InvalidOperationException($"dictionary.jsonl lines {string.Join(", ", bad)} are not {{\"term\",\"kind\"[,\"entity\"]}} with a term of at least {MinTermLength} characters and a kind of {string.Join("|", Kinds)}.");
    }

    /// <summary>The entry a matched term belongs to, or null.</summary>
    public LexiconEntry? Lookup(string matched) => this.byCanonical.GetValueOrDefault(Canonical(matched));

    private static Regex Build(IReadOnlyList<string> variants)
    {
        static string Alternation(IEnumerable<string> terms) =>
            string.Join("|", terms.OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).Select(Regex.Escape));

        var inflected = variants.Where(v => v.Length >= InflectedTermLength).ToList();
        var exact = variants.Where(v => v.Length < InflectedTermLength).ToList();
        var pattern = new StringBuilder(@"(?<![\p{L}\p{N}_])(?:");
        if (inflected.Count > 0)
        {
            pattern.Append("(?<term>").Append(Alternation(inflected)).Append(@")(?<suffix>\p{L}{0,3})");
        }

        if (exact.Count > 0)
        {
            pattern.Append(inflected.Count > 0 ? "|" : string.Empty).Append("(?<term>").Append(Alternation(exact)).Append(")(?<suffix>)");
        }

        pattern.Append(@")(?![\p{L}\p{N}_])");
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    }
}
