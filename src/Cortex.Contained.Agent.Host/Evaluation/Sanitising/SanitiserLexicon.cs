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
/// The owner's dictionary of personal terms. Matching is case-insensitive, on whole words (a letter on either side
/// blocks a match; digits and <c>_</c> do not), with up to three trailing letters for inflection for terms of four
/// or more letters, an optional genitive <c>s</c> for shorter terms (Ida → Idas) and the Russian stem rules of
/// <see cref="Stems"/> (Анна → Анну, Павел → Павла); the folded form of every term (no diacritics) is matched as well.
/// <c>public-host</c> entries are URL hosts kept
/// as they are (public sites); every other kind is pseudonymised.
/// </summary>
public sealed class SanitiserLexicon
{
    /// <summary>Known kinds.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["person", "place", "org", "handle", "other", "public-host"];

    private const int MinTermLength = 2;
    private const int InflectedTermLength = 4;
    private const string CyrillicSoftEndings = "аеёиоуыэюяйь";
    private const string CyrillicConsonants = "бвгджзклмнпрстфхцчшщ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, LexiconEntry> byCanonical;
    private readonly Dictionary<string, LexiconEntry> byStem;

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
        this.byStem = new Dictionary<string, LexiconEntry>(StringComparer.Ordinal);
        foreach (var entry in names)
        {
            foreach (var (stem, _) in Stems(entry.Term))
            {
                this.byStem.TryAdd(Canonical(stem), entry);
            }
        }

        this.Matcher = variants.Count == 0 ? null : BuildMatcher(variants);
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
    public LexiconEntry? Lookup(string matched)
    {
        var key = Canonical(matched);
        return this.byCanonical.GetValueOrDefault(key) ?? this.byStem.GetValueOrDefault(key);
    }

    /// <summary>
    /// The matcher of <paramref name="variants"/>: whole words (a letter on either side blocks a match; digits,
    /// <c>_</c> and punctuation do not, so <c>anna_cv.pdf</c> and <c>Mette2024</c> match), plus the inflection rules
    /// of <see cref="Stems"/>. Group <c>term</c> is the matched base (a variant or a stem), group <c>suffix</c> the rest.
    /// </summary>
    internal static Regex BuildMatcher(IReadOnlyList<string> variants)
    {
        static string Alternation(IEnumerable<string> terms) =>
            string.Join("|", terms.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal).Select(Regex.Escape));

        static string Branch(IEnumerable<string> terms, string suffix) => "(?<term>" + Alternation(terms) + ")(?<suffix>" + suffix + ")";

        var branches = new List<string>();
        var inflected = variants.Where(v => v.Length >= InflectedTermLength).ToList();
        var shortTerms = variants.Where(v => v.Length < InflectedTermLength).ToList();
        if (inflected.Count > 0)
        {
            branches.Add(Branch(inflected, @"\p{L}{0,3}"));
        }

        if (shortTerms.Count > 0)
        {
            branches.Add(Branch(shortTerms, "s?"));
        }

        var stems = variants.SelectMany(Stems).Distinct().ToList();
        foreach (var group in stems.GroupBy(s => s.Extra).OrderBy(g => g.Key))
        {
            branches.Add(Branch(group.Select(s => s.Stem), @"\p{L}{0," + group.Key + "}"));
        }

        var pattern = @"(?<!\p{L})(?:" + string.Join("|", branches) + @")(?!\p{L})";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The stems Russian case endings are matched on. A Cyrillic term of three or more letters ending in a vowel,
    /// <c>й</c> or <c>ь</c> also matches without that last letter plus up to two letters (Анна → Анну, Анны, Анной;
    /// Маша → Машей; Дмитрий → Дмитрия, Дмитрием); a longer term ending in <c>е</c>/<c>о</c> and a consonant loses
    /// the mobile vowel and matches with up to three letters (Павел → Павла, Павлом). Latin terms get none of this
    /// (it would turn Anna into Annual); they keep the plain rule of up to three trailing letters.
    /// </summary>
    internal static IEnumerable<(string Stem, int Extra)> Stems(string variant)
    {
        if (variant.Length < 3 || variant[^1] is not (>= '\u0400' and <= '\u04FF'))
        {
            yield break;
        }

        var last = char.ToLowerInvariant(variant[^1]);
        if (CyrillicSoftEndings.Contains(last, StringComparison.Ordinal))
        {
            yield return (variant[..^1], 2);
        }
        else if (variant.Length >= 4 && CyrillicConsonants.Contains(last, StringComparison.Ordinal) && char.ToLowerInvariant(variant[^2]) is 'е' or 'о')
        {
            yield return (variant[..^2] + variant[^1], 3);
        }
    }
}