namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// Pure classifier for Discord voice close code 4017
/// (<c>EndToEndEncryptionDAVEProtocolRequired</c>) — the gateway rejecting a
/// non-DAVE client. Surfaced so that disabling <see cref="DiscordChannelOptions.EnableVoiceDaveEncryption"/>
/// on a channel that mandates DAVE fails loudly instead of silently.
/// Best-effort text match against the Discord.Net voice log line (the close code
/// is not exposed structurally to consumers).
/// </summary>
public static class DaveRequiredCloseClassifier
{
    public static bool IsDaveRequired(string? source, string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        if (message.Contains("EndToEndEncryptionDAVEProtocolRequired", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "4017" alone is a bare, unanchored substring match — it also matches
        // coincidental numbers (e.g. "reconnect in 4017ms", a snowflake or
        // byte-count containing 4017), which would spuriously fire the loud
        // "re-enable DAVE" warning. Require close/disconnect context alongside it.
        if (!message.Contains("4017", StringComparison.Ordinal))
        {
            return false;
        }

        return message.Contains("clos", StringComparison.OrdinalIgnoreCase)
            || message.Contains("disconnect", StringComparison.OrdinalIgnoreCase);
    }
}
