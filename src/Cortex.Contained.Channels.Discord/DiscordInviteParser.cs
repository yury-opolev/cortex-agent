using System.Text.RegularExpressions;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Detects Discord invite URLs inside DM / message content and extracts the
/// invite code for later resolution. Recognizes both the short
/// <c>discord.gg/CODE</c> form and the long <c>discord.com/invite/CODE</c>
/// form, with or without a scheme, and with optional surrounding whitespace.
/// <para>
/// Used so the Discord channel handler can give the agent meaningful context
/// for voice-channel invite DMs instead of forwarding a bare URL. Without
/// this, the user's message arrives as something like
/// "https://discord.gg/TTQEUKmyn" with no indication that it's a voice
/// invitation.
/// </para>
/// </summary>
internal static partial class DiscordInviteParser
{
    [GeneratedRegex(
        @"(?:https?:\/\/)?(?:www\.)?(?:discord\.gg|discord(?:app)?\.com\/invite)\/(?<code>[A-Za-z0-9-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InviteRegex();

    /// <summary>
    /// Returns true when <paramref name="content"/> contains a recognisable
    /// Discord invite URL, and writes the invite code to
    /// <paramref name="code"/>. Returns the first match when several are
    /// present.
    /// </summary>
    public static bool TryExtractInviteCode(string? content, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var match = InviteRegex().Match(content);
        if (!match.Success)
        {
            return false;
        }

        code = match.Groups["code"].Value;
        return code.Length > 0;
    }

    /// <summary>
    /// Returns true when the message content is essentially *only* a Discord
    /// invite link — possibly with surrounding whitespace. Used as a signal
    /// that the message was produced by Discord's "Invite to Voice" UI (where
    /// the invite URL is the entire user-visible content, and the card
    /// rendering is done client-side from the link metadata) rather than a
    /// message where the user typed some text and pasted a link alongside.
    /// </summary>
    public static bool IsInviteOnly(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var trimmed = content.Trim();
        var match = InviteRegex().Match(trimmed);
        return match.Success && match.Index == 0 && match.Length == trimmed.Length;
    }
}
