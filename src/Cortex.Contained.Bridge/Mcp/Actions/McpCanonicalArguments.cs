using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cortex.Contained.Bridge.Mcp.Actions;

/// <summary>
/// Canonicalizes MCP tool-call arguments into a deterministic byte form and hashes it. The hash
/// binds a human approval to EXACT arguments, so this is a security boundary: two argument
/// documents hash identically if and only if they are semantically the same call — object key
/// order and whitespace never matter, array order and numeric lexical form always do.
/// </summary>
public static class McpCanonicalArguments
{
    /// <summary>Maximum accepted input size in UTF-8 bytes (256 KiB).</summary>
    private const int MaxInputBytes = 256 * 1024;

    /// <summary>
    /// Canonicalizes <paramref name="argumentsJson"/>:
    /// rejects input over 256 KiB, non-object roots, and duplicate property names at ANY depth;
    /// sorts object properties recursively with <see cref="StringComparer.Ordinal"/>;
    /// preserves array order; writes compact UTF-8 JSON; preserves each number's lexical
    /// representation (<c>1</c> and <c>1.0</c> are DIFFERENT approvals); and hashes the canonical
    /// bytes as <c>sha256:&lt;lowercase hex&gt;</c>.
    /// Throws <see cref="ArgumentException"/> on policy rejections and
    /// <see cref="JsonException"/> on malformed JSON.
    /// </summary>
    public static CanonicalMcpArguments Canonicalize(string argumentsJson)
    {
        ArgumentNullException.ThrowIfNull(argumentsJson);

        if (Encoding.UTF8.GetByteCount(argumentsJson) > MaxInputBytes)
        {
            throw new ArgumentException(
                $"MCP tool arguments exceed the {MaxInputBytes / 1024} KiB canonicalization limit.",
                nameof(argumentsJson));
        }

        using var document = JsonDocument.Parse(argumentsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"MCP tool arguments must be a JSON object; got {document.RootElement.ValueKind}.",
                nameof(argumentsJson));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, document.RootElement);
        }

        var canonicalBytes = buffer.WrittenSpan;
        var json = Encoding.UTF8.GetString(canonicalBytes);
        var sha256 = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(canonicalBytes))}";
        return new CanonicalMcpArguments(json, sha256);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteCanonicalObject(writer, element);
                break;

            case JsonValueKind.Array:
                // Array order is SEMANTIC — preserved exactly.
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.Number:
                // SECURITY: preserve the exact lexical representation of the number token —
                // "1" and "1.0" must bind DIFFERENT approvals, so numbers are never re-encoded.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            default:
                throw new ArgumentException($"Unsupported JSON value kind '{element.ValueKind}' in MCP tool arguments.");
        }
    }

    private static void WriteCanonicalObject(Utf8JsonWriter writer, JsonElement element)
    {
        var properties = new List<JsonProperty>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seenNames.Add(property.Name))
            {
                // A duplicate key is ambiguous: different parsers keep different occurrences, so
                // the "approved" arguments could differ from the dispatched ones. Reject outright.
                throw new ArgumentException($"MCP tool arguments contain a duplicate property name '{property.Name}'.");
            }

            properties.Add(property);
        }

        properties.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));

        writer.WriteStartObject();
        foreach (var property in properties)
        {
            writer.WritePropertyName(property.Name);
            WriteCanonical(writer, property.Value);
        }

        writer.WriteEndObject();
    }
}
