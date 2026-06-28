using System.Text.Json;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>Pure helper: parse a JSON argument object into the SDK's argument dictionary shape.</summary>
public static class McpArguments
{
    /// <summary>
    /// Parses a JSON object string into a <see cref="IReadOnlyDictionary{TKey,TValue}"/> of
    /// argument name → value (as <see cref="JsonElement"/>). Null/blank/empty-object input yields
    /// an empty dictionary. Throws <see cref="JsonException"/> on malformed JSON, or
    /// <see cref="ArgumentException"/> when the root is not a JSON object.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Parse(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("MCP tool arguments must be a JSON object.", nameof(argumentsJson));
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Clone so the value outlives the disposed JsonDocument.
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }
}
