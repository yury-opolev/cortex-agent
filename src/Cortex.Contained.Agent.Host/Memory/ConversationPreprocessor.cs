using System.Text;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Shared static utility for preprocessing conversation text before it is sent to an LLM.
/// Removes noise (e.g. base64 data URIs, oversized code blocks) and caps total length.
/// </summary>
public static class ConversationPreprocessor
{
    /// <summary>Default maximum character length for a single message passed to the LLM.</summary>
    public const int DefaultMaxMessageLength = 3_000;

    /// <summary>
    /// Runs the full preprocessing pipeline on <paramref name="text"/>:
    /// <list type="number">
    ///   <item><see cref="StripBase64DataUris"/></item>
    ///   <item><see cref="TruncateLongCodeBlocks"/></item>
    ///   <item>Cap total length at <paramref name="maxLength"/>.</item>
    /// </list>
    /// Returns <paramref name="text"/> as-is when null or empty.
    /// </summary>
    public static string SanitizeForLlm(string text, int maxLength = DefaultMaxMessageLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var processed = StripBase64DataUris(text);
        processed = TruncateLongCodeBlocks(processed);

        if (processed.Length <= maxLength)
        {
            return processed;
        }

        // Keep first half + marker + last quarter.
        var firstHalfLength = maxLength / 2;
        var lastQuarterLength = maxLength / 4;
        const string Marker = "\n... (truncated) ...\n";

        var builder = new StringBuilder(firstHalfLength + Marker.Length + lastQuarterLength);
        builder.Append(processed, 0, firstHalfLength);
        builder.Append(Marker);
        builder.Append(processed, processed.Length - lastQuarterLength, lastQuarterLength);
        return builder.ToString();
    }

    /// <summary>
    /// Replaces <c>data:...;base64,...</c> inline data URIs with <c>[base64 image removed]</c>.
    /// Looks for "data:" followed by ";base64," within 80 characters; the base64 payload is
    /// consumed until whitespace, a quote, or a closing parenthesis is reached.
    /// Returns <paramref name="text"/> as-is when null or empty.
    /// </summary>
    public static string StripBase64DataUris(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        const string DataPrefix = "data:";
        const string Base64Marker = ";base64,";
        const string Replacement = "[base64 image removed]";
        const int MaxMimeTypeLength = 80;

        var builder = new StringBuilder(text.Length);
        var position = 0;

        while (position < text.Length)
        {
            var dataIndex = text.IndexOf(DataPrefix, position, StringComparison.Ordinal);
            if (dataIndex < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            // Look for ;base64, within MaxMimeTypeLength chars after "data:"
            var searchEnd = Math.Min(dataIndex + DataPrefix.Length + MaxMimeTypeLength, text.Length);
            var base64Index = text.IndexOf(Base64Marker, dataIndex + DataPrefix.Length, searchEnd - (dataIndex + DataPrefix.Length), StringComparison.Ordinal);

            if (base64Index < 0)
            {
                // Not a data URI — copy up to and including the "data:" token and continue.
                builder.Append(text, position, dataIndex - position + DataPrefix.Length);
                position = dataIndex + DataPrefix.Length;
                continue;
            }

            // Append everything before the data URI.
            builder.Append(text, position, dataIndex - position);

            // Consume the base64 payload until a terminator character.
            var payloadStart = base64Index + Base64Marker.Length;
            var payloadEnd = payloadStart;
            while (payloadEnd < text.Length && !IsBase64Terminator(text[payloadEnd]))
            {
                payloadEnd++;
            }

            builder.Append(Replacement);
            position = payloadEnd;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Truncates fenced code blocks longer than 500 characters.
    /// The opening fence line is preserved; the body is replaced with
    /// <c>... (truncated) ...\n```\n</c>.
    /// Shorter blocks and text outside blocks are left unchanged.
    /// Returns <paramref name="text"/> as-is when null or empty or when no code blocks are present.
    /// </summary>
    public static string TruncateLongCodeBlocks(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        const string FenceMarker = "```";
        const int MaxCodeBlockLength = 500;
        const string TruncationSuffix = "... (truncated) ...\n```\n";

        var builder = new StringBuilder(text.Length);
        var position = 0;
        var foundAny = false;

        while (position < text.Length)
        {
            // Find opening fence — must be at start of line (position 0 or preceded by newline).
            var fenceStart = FindFenceStart(text, position);
            if (fenceStart < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }

            foundAny = true;

            // Find the end of the opening fence line.
            var openingLineEnd = text.IndexOf('\n', fenceStart + FenceMarker.Length);
            if (openingLineEnd < 0)
            {
                // No newline after opening fence — treat as plain text.
                builder.Append(text, position, text.Length - position);
                break;
            }

            var openingLine = text.Substring(fenceStart, openingLineEnd - fenceStart + 1); // includes '\n'

            // Find closing fence — a line that is exactly "```" (possibly with trailing whitespace).
            var bodyStart = openingLineEnd + 1;
            var closingFence = FindClosingFence(text, bodyStart);

            if (closingFence < 0)
            {
                // Unclosed block — append everything and stop.
                builder.Append(text, position, text.Length - position);
                break;
            }

            // Append text before this block.
            builder.Append(text, position, fenceStart - position);

            var body = text.Substring(bodyStart, closingFence - bodyStart);
            var closingLineEnd = text.IndexOf('\n', closingFence + FenceMarker.Length);
            var afterClose = closingLineEnd >= 0 ? closingLineEnd + 1 : text.Length;

            if (body.Length > MaxCodeBlockLength)
            {
                builder.Append(openingLine);
                builder.Append(TruncationSuffix);
            }
            else
            {
                // Preserve the full block.
                builder.Append(text, fenceStart, afterClose - fenceStart);
            }

            position = afterClose;
        }

        return foundAny ? builder.ToString() : text;
    }

    // ── Private helpers ───────────────────────────────────────────

    private static bool IsBase64Terminator(char c)
    {
        return char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == ')';
    }

    /// <summary>
    /// Finds the index of the next opening fence (``` at start of a line) at or after <paramref name="from"/>.
    /// </summary>
    private static int FindFenceStart(string text, int from)
    {
        const string FenceMarker = "```";
        var position = from;

        while (position < text.Length)
        {
            var idx = text.IndexOf(FenceMarker, position, StringComparison.Ordinal);
            if (idx < 0)
            {
                return -1;
            }

            // Must be at the beginning of a line.
            if (idx == 0 || text[idx - 1] == '\n')
            {
                return idx;
            }

            position = idx + FenceMarker.Length;
        }

        return -1;
    }

    /// <summary>
    /// Finds the start index of the closing fence (a line beginning with ```) at or after <paramref name="from"/>.
    /// </summary>
    private static int FindClosingFence(string text, int from)
    {
        const string FenceMarker = "```";
        var position = from;

        while (position < text.Length)
        {
            var idx = text.IndexOf(FenceMarker, position, StringComparison.Ordinal);
            if (idx < 0)
            {
                return -1;
            }

            // Must be at the beginning of a line.
            if (idx == 0 || text[idx - 1] == '\n')
            {
                return idx;
            }

            position = idx + FenceMarker.Length;
        }

        return -1;
    }
}
