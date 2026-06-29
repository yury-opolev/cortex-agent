using System.Text.Json;

namespace Cortex.Contained.Bridge.Setup;

/// <summary>
/// Turns GitHub's JSON error body into a human-readable string so the wizard
/// can show the real cause instead of a bare HTTP status code.
/// </summary>
public static class GitHubErrorParser
{
    /// <summary>
    /// Parses a GitHub OAuth error response body and returns a human-readable
    /// description. The expected JSON shape is:
    /// <c>{ "error": "...", "error_description": "...", "error_uri": "..." }</c>.
    /// </summary>
    /// <param name="body">The raw response body, or null.</param>
    /// <returns>
    /// A human-readable message when the body contains recognizable error fields;
    /// <see langword="null"/> when the body is null, empty, whitespace, not valid
    /// JSON, or contains neither <c>error</c> nor <c>error_description</c>.
    /// </returns>
    public static string? Describe(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // A non-object root (scalar/array, e.g. a bare "123" or "[]" from a proxy/gateway)
            // parses as valid JSON but TryGetProperty throws InvalidOperationException on it —
            // which the JsonException catch would NOT cover. Treat anything that isn't an
            // object as "no recognizable error fields".
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? error = null;
            string? errorDescription = null;

            if (root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.String)
            {
                error = errorProp.GetString();
            }

            if (root.TryGetProperty("error_description", out var descProp) &&
                descProp.ValueKind == JsonValueKind.String)
            {
                errorDescription = descProp.GetString();
            }

            if (errorDescription is not null)
            {
                if (error is not null)
                {
                    return $"{errorDescription} ({error})";
                }

                return errorDescription;
            }

            if (error is not null)
            {
                return error;
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
