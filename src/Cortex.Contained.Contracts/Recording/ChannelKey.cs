using System.Globalization;

namespace Cortex.Contained.Contracts.Recording;

/// <summary>
/// Channel-key normalisation for the runtime recording subsystem. Discord
/// voice channels use the form <c>discord:&lt;channelId&gt;</c> (lowercase
/// prefix); the local host voice channel is the literal <c>host</c>. No other
/// shapes are valid.
/// </summary>
public static class ChannelKey
{
    public const string Host = "host";

    public static string ForDiscord(ulong channelId) =>
        "discord:" + channelId.ToString(CultureInfo.InvariantCulture);

    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (string.Equals(key, Host, StringComparison.Ordinal))
        {
            return true;
        }

        const string prefix = "discord:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var tail = key[prefix.Length..];
        return tail.Length > 0
            && ulong.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
