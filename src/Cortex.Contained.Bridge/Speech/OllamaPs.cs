using System.Text.RegularExpressions;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>Reads the table printed by `ollama ps`.</summary>
internal static partial class OllamaPs
{
    /// <summary>
    /// The PROCESSOR cell of the first loaded model (<c>100% GPU</c>, <c>100% CPU</c>, <c>48%/52% CPU/GPU</c>),
    /// or null when no model is loaded or the output is not the expected table. Columns are located from the
    /// header line, because cells contain spaces.
    /// </summary>
    public static string? ParseProcessor(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            return null;
        }

        var headers = Word().Matches(lines[0]);
        var processor = headers.FirstOrDefault(h => string.Equals(h.Value, "PROCESSOR", StringComparison.Ordinal));
        if (processor is null)
        {
            return null;
        }

        var next = headers.FirstOrDefault(h => h.Index > processor.Index);
        var data = lines[1];
        if (data.Length <= processor.Index)
        {
            return null;
        }

        var end = next is not null && next.Index <= data.Length ? next.Index : data.Length;
        var cell = data[processor.Index..end].Trim();
        return cell.Length == 0 ? null : cell;
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();
}
