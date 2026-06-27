using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Contained.Agent.Host.Storage;

/// <summary>
/// Centralised helpers for the <see cref="MessageRecord.ToolCalls"/> JSON shape.
/// Used both at write time (AgentRuntime) and read time (HistoryReadTool).
/// </summary>
internal static class ToolCallSummary
{
    /// <summary>Maximum characters of arguments preserved before truncation.</summary>
    public const int MaxArgsChars = 32;

    /// <summary>Suffix appended when argument text is truncated.</summary>
    public const string TruncationSuffix = "…";

    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Truncates the raw arguments string at <see cref="MaxArgsChars"/>,
    /// appending <see cref="TruncationSuffix"/> when truncated.
    /// </summary>
    public static string TruncateArgs(string? rawArgs)
    {
        if (string.IsNullOrEmpty(rawArgs))
        {
            return string.Empty;
        }

        if (rawArgs.Length <= MaxArgsChars)
        {
            return rawArgs;
        }

        return string.Concat(rawArgs.AsSpan(0, MaxArgsChars), TruncationSuffix);
    }

    /// <summary>
    /// Serialises the entries to the JSON shape stored in <c>Messages.ToolCalls</c>.
    /// Returns null when the list is empty so the database column stays NULL.
    /// </summary>
    public static string? SerializeJson(IReadOnlyList<ToolCallSummaryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(entries, jsonOptions);
    }

    /// <summary>
    /// Parses the stored JSON. Returns an empty list for null, empty, or malformed input.
    /// </summary>
    public static IReadOnlyList<ToolCallSummaryEntry> ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<ToolCallSummaryEntry>>(json, jsonOptions);
            return parsed ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Renders the entries as a human-readable multi-line block to be appended
    /// to a Consultant turn in formatted conversation transcripts.
    /// </summary>
    public static string RenderBlock(IReadOnlyList<ToolCallSummaryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"  Tools used ({entries.Count}):");
        foreach (var entry in entries)
        {
            sb.AppendLine();
            var ok = entry.Ok ? "✓" : "✗";
            sb.Append(CultureInfo.InvariantCulture, $"  - {entry.Name}({entry.Args}) {ok}");
        }

        return sb.ToString();
    }
}
