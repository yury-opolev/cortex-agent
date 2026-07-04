using System.Text.RegularExpressions;

namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Pure placeholder renderer for system-prompt templates. Substitutes every
/// <c>{{name}}</c> whose name is present in the supplied values. Names not present
/// are left untouched (the validator guarantees templates use only known names and the
/// resolver always supplies every known name — empty string when a segment is absent).
/// </summary>
public static partial class SystemPromptRenderer
{
    [GeneratedRegex(@"\{\{([a-z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>Render <paramref name="template"/> against <paramref name="values"/>.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        return PlaceholderRegex().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            return values.TryGetValue(name, out var value) ? value : match.Value;
        });
    }
}
