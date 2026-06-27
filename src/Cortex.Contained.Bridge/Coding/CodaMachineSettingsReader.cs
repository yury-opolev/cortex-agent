using System.Text.Json;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Reads the user's machine-level <c>~/.coda/settings.json</c> top-level
/// <c>defaultProvider</c> / <c>defaultModel</c> so the Bridge can spawn coda with the same
/// provider the user configured for the standalone TUI. Best-effort: missing or malformed
/// files yield nulls.
/// </summary>
public static class CodaMachineSettingsReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static (string? Provider, string? Model) Read(string? userHomeDir = null)
    {
        var home = userHomeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var file = Path.Combine(home, ".coda", "settings.json");
        if (!File.Exists(file))
        {
            return (null, null);
        }

        try
        {
            var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(file), JsonOptions);
            return (Normalize(doc?.DefaultProvider), Normalize(doc?.DefaultModel));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, null);
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class Document
    {
        public string? DefaultProvider { get; set; }
        public string? DefaultModel { get; set; }
    }
}
