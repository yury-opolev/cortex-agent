namespace Cortex.Contained.Speech.Tts;

using System.Collections.Frozen;
using Lingua;

/// <summary>
/// <see cref="ILanguageDetector"/> backed by SearchPioneer.Lingua. Restricted to a
/// caller-supplied set of ISO 639-1 candidate codes (typically the languages
/// configured in <c>cortex.yml</c>'s <c>speech.tts.languages</c>). In-process,
/// no network, milliseconds-cheap on short text.
/// </summary>
public sealed class LinguaLanguageDetector : ILanguageDetector
{
    private static readonly FrozenDictionary<string, Language> codeToLingua = BuildCodeMap();
    private static readonly FrozenDictionary<Language, string> linguaToCode = BuildReverseMap();

    private readonly LanguageDetector detector;
    private readonly FrozenSet<string> configuredCodes;
    private readonly string fallback;

    /// <summary>
    /// Initializes a new instance of <see cref="LinguaLanguageDetector"/>.
    /// </summary>
    /// <param name="isoCodes">
    /// Ordered list of ISO 639-1 codes to consider. The first recognized code
    /// is used as the default <see cref="Fallback"/> when <paramref name="fallback"/> is <see langword="null"/>.
    /// </param>
    /// <param name="fallback">
    /// Explicit fallback ISO 639-1 code. Defaults to the first entry in <paramref name="isoCodes"/>.
    /// </param>
    public LinguaLanguageDetector(IReadOnlyList<string> isoCodes, string? fallback = null)
    {
        var mapped = new List<Language>(isoCodes.Count);
        var codes = new List<string>(isoCodes.Count);
        foreach (var code in isoCodes)
        {
            if (codeToLingua.TryGetValue(code, out var lang))
            {
                mapped.Add(lang);
                codes.Add(code);
            }
        }

        // Lingua requires at least two languages to disambiguate; pad if needed.
        if (mapped.Count < 2)
        {
            if (!codes.Contains("en", StringComparer.Ordinal))
            {
                mapped.Add(Language.English);
                codes.Add("en");
            }

            if (mapped.Count < 2)
            {
                mapped.Add(Language.Danish);
                codes.Add("da");
            }
        }

        this.configuredCodes = codes.ToFrozenSet(StringComparer.Ordinal);
        this.detector = LanguageDetectorBuilder.FromLanguages([.. mapped]).Build();
        this.fallback = fallback ?? codes[0];
    }

    /// <inheritdoc/>
    public string Fallback => this.fallback;

    /// <inheritdoc/>
    public string DetectTop(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return this.fallback;
        }

        var detected = this.detector.DetectLanguageOf(text);
        return linguaToCode.TryGetValue(detected, out var code) && this.configuredCodes.Contains(code)
            ? code
            : this.fallback;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, double> DetectConfidences(string text)
    {
        var dict = new Dictionary<string, double>(this.configuredCodes.Count, StringComparer.Ordinal);
        foreach (var code in this.configuredCodes)
        {
            dict[code] = 0d;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            dict[this.fallback] = 1d;
            return dict;
        }

        var values = this.detector.ComputeLanguageConfidenceValues(text);
        foreach (var kv in values)
        {
            if (linguaToCode.TryGetValue(kv.Key, out var code) && this.configuredCodes.Contains(code))
            {
                dict[code] = kv.Value;
            }
        }

        return dict;
    }

    private static FrozenDictionary<string, Language> BuildCodeMap() =>
        new Dictionary<string, Language>(StringComparer.Ordinal)
        {
            ["en"] = Language.English,
            ["da"] = Language.Danish,
            ["ru"] = Language.Russian,
            ["de"] = Language.German,
            ["fr"] = Language.French,
            ["es"] = Language.Spanish,
            ["it"] = Language.Italian,
            ["nl"] = Language.Dutch,
            ["pt"] = Language.Portuguese,
            ["sv"] = Language.Swedish,
            ["no"] = Language.Bokmal,
            ["fi"] = Language.Finnish,
            ["pl"] = Language.Polish,
            ["uk"] = Language.Ukrainian,
            ["zh"] = Language.Chinese,
            ["ja"] = Language.Japanese,
            ["ko"] = Language.Korean,
            ["kk"] = Language.Kazakh,
        }.ToFrozenDictionary();

    private static FrozenDictionary<Language, string> BuildReverseMap()
    {
        var dict = new Dictionary<Language, string>();
        foreach (var kv in codeToLingua)
        {
            dict[kv.Value] = kv.Key;
        }

        return dict.ToFrozenDictionary();
    }
}
