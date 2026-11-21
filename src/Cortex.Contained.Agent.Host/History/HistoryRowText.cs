using System.Text.RegularExpressions;
using Cortex.Contained.Agent.Host.Storage;

namespace Cortex.Contained.Agent.Host.History;

/// <summary>
/// The text of one stored row as the index sees it (folded content + tool names, no timestamps
/// or role labels) and as recall tools display it (attachment markers normalised).
/// </summary>
public static partial class HistoryRowText
{
    /// <summary>The folded indexed body of <paramref name="row"/>.</summary>
    public static string IndexedBody(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var content = DisplayContent(row);
        var toolNames = ToolCallSummary.ParseJson(row.ToolCalls).Select(e => e.Name).ToList();
        return TextFolder.Fold(toolNames.Count == 0 ? content : content + "\n" + string.Join(' ', toolNames));
    }

    /// <summary>Content with <c>[image attached: mime]</c> replaced by <c>[attachment:mime]</c>.</summary>
    public static string DisplayContent(MessageRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return AttachmentMarker().Replace(row.Content, m => "[attachment:" + m.Groups["mime"].Value + "]");
    }

    [GeneratedRegex(@"\[image attached: (?<mime>[^\]\r\n]{1,100})\]", RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentMarker();
}
