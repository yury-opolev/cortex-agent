using System.Text;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Pipeline;

/// <summary>
/// Sanitizes inbound message content to defend against prompt injection attacks.
/// Applies multiple layers of defense:
/// 1. Strips control characters (except \n, \r, \t)
/// 2. Validates and repairs UTF-8 encoding
/// 3. Strips known injection patterns
/// 4. Adds boundary markers to user content
/// 5. Truncates excessively long messages
/// </summary>
public static partial class ContentSanitizer
{
    /// <summary>Maximum user message length (characters).</summary>
    private const int MaxMessageLength = 32_000;

    /// <summary>Boundary marker to separate user content from system prompts.</summary>
    private const string UserBoundaryStart = "--- USER MESSAGE START ---";
    private const string UserBoundaryEnd = "--- USER MESSAGE END ---";

    /// <summary>
    /// Sanitize user message content before passing to the LLM.
    /// </summary>
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sanitized = input;

        // 1. Strip control characters (keep newlines, tabs, carriage returns)
        sanitized = StripControlCharacters(sanitized);

        // 2. Validate and clean UTF-8
        sanitized = ValidateUtf8(sanitized);

        // 3. Strip known system/assistant role injection attempts
        sanitized = SystemRoleInjectionPattern().Replace(sanitized, "[FILTERED]");
        sanitized = AssistantRoleInjectionPattern().Replace(sanitized, "[FILTERED]");

        // 4. Strip attempts to override instructions
        sanitized = IgnoreInstructionsPattern().Replace(sanitized, "[FILTERED]");

        // 5. Truncate if too long
        if (sanitized.Length > MaxMessageLength)
        {
            sanitized = sanitized[..MaxMessageLength] + "\n[Message truncated]";
        }

        return sanitized;
    }

    /// <summary>
    /// Wrap user content with boundary markers for the LLM prompt.
    /// </summary>
    public static string WrapWithBoundary(string sanitizedContent)
    {
        return $"{UserBoundaryStart}\n{sanitizedContent}\n{UserBoundaryEnd}";
    }

    /// <summary>
    /// Removes ASCII control characters (0x00-0x1F, 0x7F) except for
    /// newline (0x0A), carriage return (0x0D), and tab (0x09).
    /// Also removes Unicode "other" control characters (e.g., RTL overrides,
    /// zero-width joiners used for homoglyph attacks).
    /// </summary>
    internal static string StripControlCharacters(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c == '\n' || c == '\r' || c == '\t')
            {
                sb.Append(c);
                continue;
            }

            // Skip ASCII control characters and DEL
            if (c < 0x20 || c == 0x7F)
            {
                continue;
            }

            // Skip Unicode format/control characters
            var category = char.GetUnicodeCategory(c);
            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Validates UTF-8 encoding by re-encoding through UTF-8 bytes.
    /// Replaces any invalid sequences with the Unicode replacement character.
    /// </summary>
    internal static string ValidateUtf8(string input)
    {
        // Encode to UTF-8 bytes using a replacing encoder, then decode back.
        // This strips any invalid surrogate pairs or broken sequences.
        var bytes = Encoding.UTF8.GetBytes(input);
        return Encoding.UTF8.GetString(bytes);
    }

    [GeneratedRegex(@"(?i)(\bsystem\s*:\s*|<\|system\|>|<<\s*SYS\s*>>)", RegexOptions.Compiled)]
    private static partial Regex SystemRoleInjectionPattern();

    [GeneratedRegex(@"(?i)(\bassistant\s*:\s*|<\|assistant\|>)", RegexOptions.Compiled)]
    private static partial Regex AssistantRoleInjectionPattern();

    [GeneratedRegex(@"(?i)(ignore\s+(all\s+)?(previous|above|prior)\s+(instructions?|prompts?|rules?))", RegexOptions.Compiled)]
    private static partial Regex IgnoreInstructionsPattern();
}
