using System.Text;
using System.Text.RegularExpressions;
using Cortex.Contained.Contracts.Security;

namespace Cortex.Contained.Agent.Host.Evaluation.Sanitising;

/// <summary>
/// Deterministic pseudonymisation of corpus and query text for the off-machine copy (D6). Detectors, in
/// priority order on the same text (overlaps go to the earlier detector):
/// <list type="number">
/// <item>URLs: the scheme stays, the host is kept only when the dictionary lists it as <c>public-host</c>
/// (otherwise <c>host….example</c>), path, query and fragment become one pseudonym;</item>
/// <item>personal identifiers of <see cref="SensitiveDataRedactor.FindPersonalIdentifiers"/> (IBAN, card, CPR,
/// email, GPS, phone) — one implementation with the persistence redactor;</item>
/// <item><c>@handles</c>;</item>
/// <item>owner dictionary terms (names, places, organisations), inflection suffix kept;</item>
/// <item>long numbers (9 digits or more: chat ids, account and order numbers), replaced by digits of the same length.</item>
/// </list>
/// Dates, times, prices and short numbers are kept (temporal and identifier queries depend on them). The text is
/// NFKC-normalised and stripped of zero-width characters first, so obfuscated spellings cannot slip through.
/// </summary>
public sealed partial class TextSanitiser
{
    private readonly SanitiserLexicon dictionary;
    private readonly PseudonymMap map;

    public TextSanitiser(SanitiserLexicon dictionary, PseudonymMap map)
    {
        this.dictionary = dictionary;
        this.map = map;
    }

    /// <summary>Every pseudonym produced (the leakage check skips them).</summary>
    public IReadOnlyCollection<string> Pseudonyms => this.map.Pseudonyms;

    /// <summary>NFKC plus removal of zero-width characters (the sanitiser and the leakage check see the same text).</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return normalized.AsSpan().IndexOfAny("\u200B\u200C\u200D\u2060\uFEFF") < 0
            ? normalized
            : string.Concat(normalized.Where(c => c is not ('\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF')));
    }

    /// <summary>The sanitised form of <paramref name="text"/>.</summary>
    public string Sanitise(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var input = Normalize(text);
        var spans = new List<(int Index, int Length, string Replacement)>();
        bool Free(int index, int length) => !spans.Exists(s => s.Index < index + length && index < s.Index + s.Length);

        foreach (Match url in UrlPattern().Matches(input))
        {
            spans.Add((url.Index, url.Length, this.Url(url)));
        }

        foreach (var match in SensitiveDataRedactor.FindPersonalIdentifiers(input))
        {
            if (Free(match.Index, match.Length))
            {
                var value = input.Substring(match.Index, match.Length);
                spans.Add((match.Index, match.Length, this.map.For(match.Kind, Key(value, match.Kind))));
            }
        }

        foreach (Match handle in HandlePattern().Matches(input))
        {
            if (Free(handle.Index, handle.Length))
            {
                spans.Add((handle.Index, handle.Length, "@" + this.map.For("handle", handle.Groups["name"].Value.ToLowerInvariant())));
            }
        }

        if (this.dictionary.Matcher is { } matcher)
        {
            foreach (Match term in matcher.Matches(input))
            {
                if (Free(term.Index, term.Length) && this.dictionary.Lookup(term.Groups["term"].Value) is { } entry)
                {
                    var pseudonym = this.map.For(entry.Kind, SanitiserLexicon.Canonical(entry.Entity ?? entry.Term));
                    spans.Add((term.Index, term.Length, pseudonym + term.Groups["suffix"].Value));
                }
            }
        }

        foreach (Match number in LongNumberPattern().Matches(input))
        {
            if (Free(number.Index, number.Length))
            {
                spans.Add((number.Index, number.Length, this.map.For("number", number.Value)));
            }
        }

        if (spans.Count == 0)
        {
            return input;
        }

        spans.Sort((a, b) => a.Index.CompareTo(b.Index));
        var output = new StringBuilder(input.Length);
        var position = 0;
        foreach (var (index, length, replacement) in spans)
        {
            output.Append(input, position, index - position).Append(replacement);
            position = index + length;
        }

        return output.Append(input, position, input.Length - position).ToString();
    }

    /// <summary>A channel or user id: the letter prefix stays (<c>discord-dm-</c>, <c>webchat</c>), numbers and dictionary terms are pseudonymised.</summary>
    public string SanitiseIdentifier(string? id) => this.Sanitise(id);

    private static string Key(string value, string kind) => kind switch
    {
        SensitiveDataRedactor.IdentifierKinds.Email => value.ToLowerInvariant(),
        SensitiveDataRedactor.IdentifierKinds.Gps => value,
        SensitiveDataRedactor.IdentifierKinds.Phone => PhoneKey(value),
        _ => new string(value.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant(),
    };

    // One number, one pseudonym: +45 12 34 56 78, 0045 1234 5678 and 12345678 share a key (a Danish country code is dropped).
    private static string PhoneKey(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        return digits.Length == 10 && digits.StartsWith("45", StringComparison.Ordinal) ? digits[2..] : digits;
    }

    private string Url(Match url)
    {
        var host = url.Groups["host"].Value.ToLowerInvariant();
        var rest = url.Groups["rest"].Value;
        var keptHost = this.dictionary.PublicHosts.Contains(host) ? host : this.map.For("host", host);
        return url.Groups["scheme"].Value + "://" + keptHost + (rest.Length > 1 ? "/" + this.map.For("path", rest) : string.Empty);
    }

    [GeneratedRegex(@"(?<scheme>https?)://(?<host>[A-Za-z0-9.\-]+)(?::\d+)?(?<rest>[/?#][^\s<>""'\)\]]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    // A handle has at least one letter; an all-digit mention (<@123…>) is a chat id and goes to the number rule.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_@.])@(?<name>(?=[A-Za-z0-9_.]*[A-Za-z_])[A-Za-z0-9_.]{2,32})(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant)]
    private static partial Regex HandlePattern();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{9,}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumberPattern();
}
