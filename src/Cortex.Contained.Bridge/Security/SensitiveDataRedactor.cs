using System.Text.RegularExpressions;

namespace Cortex.Contained.Bridge.Security;

/// <summary>
/// Redacts sensitive data patterns from text before it is logged.
/// Handles API keys, tokens, phone numbers, and long Base64 strings.
/// </summary>
internal static partial class SensitiveDataRedactor
{
    private const string RedactedPlaceholder = "[REDACTED]";

    /// <summary>
    /// Redacts all known sensitive patterns from the input string.
    /// </summary>
    internal static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input ?? string.Empty;
        }

        var result = input;

        // Order matters: redact key-value patterns first (most specific),
        // then structured identifiers, then general patterns.

        // 1. Key-value pairs: "api_key=xxx", "token: xxx", "password=xxx", etc.
        result = KeyValueSecretPattern().Replace(result, static m =>
            $"{m.Groups[1].Value}{m.Groups[2].Value}{RedactedPlaceholder}");

        // 2. Common API key prefixes (OpenAI sk-..., Anthropic sk-ant-..., etc.)
        result = ApiKeyPrefixPattern().Replace(result, RedactedPlaceholder);

        // 3. Phone numbers (10-15 digits, optionally prefixed with +)
        result = PhoneNumberPattern().Replace(result, RedactedPlaceholder);

        // 4. Long Base64 strings (40+ chars — likely tokens or keys)
        result = LongBase64Pattern().Replace(result, RedactedPlaceholder);

        return result;
    }

    // Key-value patterns: api_key=value, token: value, secret=value, password=value, credential=value
    [GeneratedRegex(
        @"(?i)(api[_\-]?key|token|secret|password|credential)(\s*[=:]\s*)\S+",
        RegexOptions.Compiled)]
    private static partial Regex KeyValueSecretPattern();

    // Common API key prefixes: sk-..., sk-ant-..., key-... (at least 20 chars total)
    [GeneratedRegex(
        @"\b(?:sk-[a-zA-Z0-9_\-]{20,}|sk-ant-[a-zA-Z0-9_\-]{20,}|key-[a-zA-Z0-9_\-]{20,})\b",
        RegexOptions.Compiled)]
    private static partial Regex ApiKeyPrefixPattern();

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
