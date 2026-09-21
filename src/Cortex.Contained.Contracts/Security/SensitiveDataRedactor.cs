using System.Text.RegularExpressions;

namespace Cortex.Contained.Contracts.Security;

/// <summary>
/// Redacts sensitive data patterns from text before it is logged or persisted.
/// Handles API keys, tokens, phone numbers, and long Base64 strings.
/// <para>
/// Lives in Contracts so the Bridge and the Agent Host share ONE implementation. Two copies of
/// security-critical redaction is how one of them silently goes stale — a pattern added on one
/// side of the process boundary would not protect the other.
/// </para>
/// <para>
/// This is a redactor, not a suppressor: only the sensitive substring is replaced and the
/// surrounding text survives verbatim. Callers that need the text to stay readable — the autonomy
/// assumption ledger, for instance, which IS the report of an unattended run — depend on that.
/// Contrast <c>McpTelemetrySanitizer</c>, which deliberately discards a whole payload.
/// </para>
/// </summary>
public static partial class SensitiveDataRedactor
{
    private const string RedactedPlaceholder = "[REDACTED]";

    /// <summary>
    /// Redacts all known sensitive patterns from the input string.
    /// </summary>
    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = input;

        // Order matters: redact key-value patterns first (most specific),
        // then structured identifiers, then general patterns.

        // 1. Key-value pairs: "api_key=xxx", "token: xxx", "******", etc.
        result = KeyValueSecretPattern().Replace(result, static m =>
            $"{m.Groups[1].Value}{m.Groups[2].Value}{RedactedPlaceholder}");

        // 2. Common API key prefixes (OpenAI sk-..., Anthropic sk-ant-..., etc.)
        result = ApiKeyPrefixPattern().Replace(result, RedactedPlaceholder);

        // 2b. Other provider token shapes. Added because this redactor is no longer log-only:
        // the autonomy assumption ledger runs unbounded, model-authored prose through it and then
        // EXPORTS the result to a chat channel, so a miss is a secret delivered to Discord rather
        // than a secret in a local log line.
        result = ProviderTokenPattern().Replace(result, RedactedPlaceholder);

        // 2c. Secret keywords in prose, with no '=' or ':' separator — "the password is hunter2",
        // "logged in with token abc123". The key-value pattern above cannot see these.
        result = ProseSecretPattern().Replace(result, static m =>
            $"{m.Groups[1].Value}{m.Groups[2].Value}{RedactedPlaceholder}");

        // 3. Phone numbers (10-15 digits, optionally prefixed with +)
        result = PhoneNumberPattern().Replace(result, RedactedPlaceholder);

        // 4. Long Base64 strings (40+ chars — likely tokens or keys)
        result = LongBase64Pattern().Replace(result, RedactedPlaceholder);

        return result;
    }

    // Key-value patterns: api_key=value, token: value, secret=value, ****** credential=value
    [GeneratedRegex(
        @"(?i)(api[_\-]?key|token|secret|password|credential)(\s*[=:]\s*)\S+",
        RegexOptions.Compiled)]
    private static partial Regex KeyValueSecretPattern();

    // Common API key prefixes: sk-..., sk-ant-..., key-... (at least 20 chars total)
    [GeneratedRegex(
        @"\b(?:sk-[a-zA-Z0-9_\-]{20,}|sk-ant-[a-zA-Z0-9_\-]{20,}|key-[a-zA-Z0-9_\-]{20,})\b",
        RegexOptions.Compiled)]
    private static partial Regex ApiKeyPrefixPattern();

    // Provider token shapes the prefix pattern misses. Deliberately not \b-anchored on the left
    // for underscore-bearing prefixes: '_' is a word character, so \b never matches between
    // "ghp" and "_", which is why ghp_... survived the base patterns.
    [GeneratedRegex(
        @"(?:gh[pousr]_[A-Za-z0-9]{16,}|xox[baprs]-[A-Za-z0-9\-]{10,}|glpat-[A-Za-z0-9_\-]{16,}|AIza[0-9A-Za-z_\-]{30,}|(?:AKIA|ASIA)[0-9A-Z]{12,}|-----BEGIN[A-Z ]*PRIVATE KEY-----)",
        RegexOptions.Compiled)]
    private static partial Regex ProviderTokenPattern();

    // Secret keywords followed by a value in prose rather than a key=value pair.
    [GeneratedRegex(
        @"(?i)\b(password|passphrase|api[_\-]?key|token|secret|credential)\b(\s+(?:is|was|=|:)?\s*)(?!\s)[^\s,.;]{4,}",
        RegexOptions.Compiled)]
    private static partial Regex ProseSecretPattern();

    // Phone numbers: +1234567890 or 1234567890123 (10-15 digits)
    [GeneratedRegex(
        @"(?<!\w)\+?\d{10,15}(?!\w)",
        RegexOptions.Compiled)]
    private static partial Regex PhoneNumberPattern();

    // Long Base64 strings (40+ alphanumeric/+/= chars — likely tokens or encrypted data)
    [GeneratedRegex(
        @"\b[A-Za-z0-9+/]{40,}={0,3}\b",
        RegexOptions.Compiled)]
    private static partial Regex LongBase64Pattern();
}
