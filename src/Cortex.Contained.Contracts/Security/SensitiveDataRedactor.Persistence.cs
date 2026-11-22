using System.Text;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Contracts.Security;

/// <summary>
/// Redaction for text that is PERSISTED (event payloads), stricter than log redaction: the input
/// is unescaped and normalised first so encoded secrets cannot slip past the patterns, known
/// secret values are replaced exactly, and personal identifiers (Danish CPR numbers, IBANs,
/// Luhn-valid card numbers, emails, Danish phone numbers, GPS coordinates) are removed as well.
/// </summary>
public static partial class SensitiveDataRedactor
{
    /// <summary>Stored instead of a payload when the known-secret set is not loaded yet.</summary>
    public const string RedactionUnavailable = "[REDACTION_UNAVAILABLE]";

    private const int MinKnownSecretLength = 8;

    /// <summary>
    /// Normalises <paramref name="input"/>, replaces every value in <paramref name="knownSecrets"/>,
    /// then applies <see cref="Redact"/> and the personal-identifier patterns.
    /// </summary>
    public static string RedactForPersistence(string? input, IEnumerable<string> knownSecrets)
    {
        ArgumentNullException.ThrowIfNull(knownSecrets);
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = Normalize(input);
        foreach (var secret in knownSecrets.Where(s => s.Length >= MinKnownSecretLength).OrderByDescending(s => s.Length))
        {
            result = result.Replace(secret, "[REDACTED:secret]", StringComparison.Ordinal);
        }

        result = IbanPattern().Replace(result, static m => IsValidIban(m.Value) ? "[REDACTED:iban]" : m.Value);
        result = CardPattern().Replace(result, static m => IsLuhnValid(m.Value) ? "[REDACTED:card]" : m.Value);
        result = CprPattern().Replace(result, "[REDACTED:cpr]");
        result = EmailPattern().Replace(result, "[REDACTED:email]");
        result = GpsPattern().Replace(result, "[REDACTED:gps]");
        result = DanishPhonePattern().Replace(result, "[REDACTED:phone]");
        return Redact(result);
    }

    /// <summary>JSON/regex-style unescape, NFKC, and removal of zero-width characters.</summary>
    internal static string Normalize(string input)
    {
        string unescaped;
        try
        {
            unescaped = Regex.Unescape(input);
        }
        catch (ArgumentException)
        {
            unescaped = input;
        }

        var normalized = unescaped.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (c is not ('\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF'))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsLuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    private static bool IsValidIban(string candidate)
    {
        var compact = candidate.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        if (compact.Length is < 15 or > 34)
        {
            return false;
        }

        var rearranged = compact[4..] + compact[..4];
        var remainder = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value > 9
                ? ((remainder * 100) + value) % 97
                : ((remainder * 10) + value) % 97;
        }

        return remainder == 1;
    }

    // IBAN: country code, check digits, then 11-30 alphanumerics, optionally grouped by spaces.
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:[ ]?[A-Z0-9]){11,30}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IbanPattern();

    // Card numbers: 13-19 digits, optionally grouped by spaces or dashes (validated by Luhn).
    [GeneratedRegex(@"(?<!\d)\d(?:[ -]?\d){12,18}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CardPattern();

    // Danish CPR: DDMMYY-XXXX (separator optional).
    [GeneratedRegex(@"(?<!\d)(?:0[1-9]|[12]\d|3[01])(?:0[1-9]|1[0-2])\d{2}[- ]?\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex CprPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    // Decimal latitude,longitude pairs with at least three decimals.
    [GeneratedRegex(@"[-+]?\d{1,2}\.\d{3,}\s*,\s*[-+]?\d{1,3}\.\d{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex GpsPattern();

    // Danish phone numbers: optional +45/0045, then 8 digits optionally grouped in pairs or 4+4.
    [GeneratedRegex(@"(?<![\d+])(?:(?:\+|00)45[ ]?)?(?:\d{2}[ ]?\d{2}[ ]?\d{2}[ ]?\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DanishPhonePattern();
}
