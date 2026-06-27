using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// Pure text-shaping helpers that convert a chat history into the prompt
/// string the LiveKit multilingual turn detector expects. Ports
/// <c>_EUORunnerBase._normalize_text</c> + <c>_format_chat_ctx</c> from the
/// LiveKit Python plugin. No ONNX / tokenizer dependency — kept isolated so
/// the text logic is unit-testable without the model loaded.
/// </summary>
internal static class LiveKitPromptBuilder
{
    private const int MaxHistoryTurns = 6;
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// NFKC + lowercase + strip Unicode-category-P punctuation except apostrophe
    /// and hyphen + collapse whitespace. Matches the multilingual variant of
    /// <c>_EUORunnerBase._normalize_text</c>.
    /// </summary>
    public static string NormalizeMultilingual(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            var isPunct = cat == UnicodeCategory.ConnectorPunctuation
                || cat == UnicodeCategory.DashPunctuation
                || cat == UnicodeCategory.OpenPunctuation
                || cat == UnicodeCategory.ClosePunctuation
                || cat == UnicodeCategory.InitialQuotePunctuation
                || cat == UnicodeCategory.FinalQuotePunctuation
                || cat == UnicodeCategory.OtherPunctuation;

            if (isPunct && ch != '\'' && ch != '-')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return WhitespaceRegex.Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// Build the turn-detector prompt string from a chat history:
    /// <list type="number">
    ///   <item>Drop anything not user/assistant.</item>
    ///   <item>Normalize each message's content.</item>
    ///   <item>Drop messages that become empty after normalization.</item>
    ///   <item>Merge consecutive same-role messages with a single space.</item>
    ///   <item>Keep only the last <see cref="MaxHistoryTurns"/> turns.</item>
    ///   <item>Render Qwen chat template; strip the trailing <c>&lt;|im_end|&gt;</c>
    ///         on the final message — the model's job is to predict whether it
    ///         would emit one next.</item>
    /// </list>
    /// </summary>
    public static string BuildPrompt(IReadOnlyList<TurnDetectorMessage> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        if (turns.Count == 0)
        {
            return string.Empty;
        }

        var filtered = new List<(string Role, string Content)>(turns.Count);
        foreach (var turn in turns)
        {
            if (turn.Role != "user" && turn.Role != "assistant")
            {
                continue;
            }

            var normalized = NormalizeMultilingual(turn.Content);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (filtered.Count > 0 && filtered[^1].Role == turn.Role)
            {
                var merged = filtered[^1];
                filtered[^1] = (merged.Role, merged.Content + " " + normalized);
            }
            else
            {
                filtered.Add((turn.Role, normalized));
            }
        }

        if (filtered.Count == 0)
        {
            return string.Empty;
        }

        if (filtered.Count > MaxHistoryTurns)
        {
            filtered = filtered.GetRange(filtered.Count - MaxHistoryTurns, MaxHistoryTurns);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < filtered.Count; i++)
        {
            var (role, content) = filtered[i];
            sb.Append("<|im_start|>").Append(role).Append('\n').Append(content);
            if (i < filtered.Count - 1)
            {
                sb.Append("<|im_end|>\n");
            }
        }

        return sb.ToString();
    }
}
