using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Llm;

/// <summary>
/// Renders a raw provider error message — formatted by the LLM clients as
/// <c>"HTTP {status}: {body}"</c> or a transport message — into a concise,
/// user-facing line. The raw body (which can be a full HTML error page, e.g.
/// GitHub's "Unicorn!" 502) is kept in the logs but never shown to a channel.
/// </summary>
public static partial class LlmErrorPresenter
{
    private const int MaxLength = 200;
    private const string Generic = "The model provider returned an error.";

    public static string ToUserMessage(string? rawErrorMessage)
    {
        if (string.IsNullOrWhiteSpace(rawErrorMessage))
        {
            return Generic;
        }

        var match = HttpErrorRegex().Match(rawErrorMessage);
        if (match.Success
            && int.TryParse(
                match.Groups[1].Value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            var hint = status switch
            {
                >= 500 and <= 599 => " — a temporary provider-side error.",
                429 => " — the provider is rate-limiting requests.",
                401 or 403 => " — an authentication or permission problem.",
                _ => ".",
            };
            return $"The model provider returned an error (HTTP {status}){hint}";
        }

        // Non-HTTP (transport/timeout/other): strip any markup, collapse whitespace,
        // take the first line, and truncate — never echo a raw body verbatim.
        var cleaned = Collapse(StripTags(rawErrorMessage));
        if (cleaned.Length > MaxLength)
        {
            cleaned = cleaned[..MaxLength].TrimEnd() + "…";
        }

        return cleaned.Length == 0 ? Generic : $"The model provider request failed: {cleaned}";
    }

    private static string StripTags(string input) => TagRegex().Replace(input, " ");

    private static string Collapse(string input) => WhitespaceRegex().Replace(input, " ").Trim();

    [GeneratedRegex(@"^HTTP (\d+): ?(.*)$", RegexOptions.Singleline)]
    private static partial Regex HttpErrorRegex();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
