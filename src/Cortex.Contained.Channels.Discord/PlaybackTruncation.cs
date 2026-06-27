using System.Text;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure: reconstruct the text the user actually heard before barge-in, ending
/// with a "…" marker. Fully-played sentences are exact; the interrupted
/// sentence is cut at round(ratio*len) snapped back to a word boundary.
/// </summary>
internal static class PlaybackTruncation
{
    public const string Ellipsis = "…";

    public static string BuildPlayedText(PlaybackProgress p)
    {
        var sb = new StringBuilder();
        foreach (var s in p.FullyPlayedSentences)
        {
            if (sb.Length > 0) { sb.Append(' '); }
            sb.Append(s.Trim());
        }

        if (!string.IsNullOrEmpty(p.InterruptedSentenceText))
        {
            var ratio = Math.Clamp(p.InterruptedPlayedRatio, 0.0, 1.0);
            var text = p.InterruptedSentenceText;
            var cut = (int)Math.Round(ratio * text.Length, MidpointRounding.AwayFromZero);
            cut = Math.Clamp(cut, 0, text.Length);

            if (cut < text.Length)
            {
                // Snap back to the last whitespace at or before the cut so we
                // never split a word. If none, drop the partial entirely.
                var snap = text.LastIndexOf(' ', Math.Max(0, cut - 1));
                cut = snap < 0 ? 0 : snap;
            }

            var partial = text[..cut].Trim();
            if (partial.Length > 0)
            {
                if (sb.Length > 0) { sb.Append(' '); }
                sb.Append(partial);
            }
        }

        if (sb.Length > 0) { sb.Append(' '); }
        // Ellipsis is a single UTF-16 char (U+2026); append as char to satisfy
        // CA1834 (constant unit string) while keeping the public const string.
        sb.Append(Ellipsis[0]);
        return sb.ToString();
    }
}
