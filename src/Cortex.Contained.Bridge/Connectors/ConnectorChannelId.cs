namespace Cortex.Contained.Bridge.Connectors;

/// <summary>Utilities for constructing and validating plugin channel identifiers.</summary>
public static class ConnectorChannelId
{
    /// <summary>The prefix segment for all plugin channel IDs.</summary>
    public const string Prefix = "plugin";

    /// <summary>Creates a channel ID from a connector key and instance ID.</summary>
    /// <param name="key">Connector type key (e.g. <c>terminal</c>).</param>
    /// <param name="instanceId">Connector instance identifier (e.g. <c>default</c>).</param>
    public static string Create(string key, string instanceId) => $"plugin:{key}:{instanceId}";

    /// <summary>
    /// Attempts to parse a plugin channel ID into its constituent key and instance ID.
    /// Returns false for anything that is not exactly three colon-separated segments
    /// where the first is <c>plugin</c> and the remaining two pass <see cref="IsValidSegment"/>.
    /// </summary>
    public static bool TryParse(string channelId, out string? key, out string? instanceId)
    {
        key = null;
        instanceId = null;

        if (string.IsNullOrEmpty(channelId))
        {
            return false;
        }

        var parts = channelId.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsValidSegment(parts[1]) || !IsValidSegment(parts[2]))
        {
            return false;
        }

        key = parts[1];
        instanceId = parts[2];
        return true;
    }

    /// <summary>Returns true when <paramref name="channelId"/> is a valid plugin channel ID.</summary>
    public static bool IsPluginChannelId(string channelId) =>
        TryParse(channelId, out _, out _);

    /// <summary>
    /// Returns true when <paramref name="segment"/> is non-null, between 1 and 64 characters,
    /// and contains only ASCII lower-case letters, digits, hyphens, and underscores.
    /// </summary>
    public static bool IsValidSegment(string? segment)
    {
        if (segment is null || segment.Length is 0 or > 64)
        {
            return false;
        }

        foreach (var ch in segment)
        {
            if (!IsValidSegmentChar(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lower-cases and trims <paramref name="segment"/>, then validates it.
    /// Returns the normalised value when valid; null otherwise.
    /// </summary>
    public static string? Normalize(string? segment)
    {
        if (segment is null)
        {
            return null;
        }

        var normalized = segment.Trim().ToLowerInvariant();
        return IsValidSegment(normalized) ? normalized : null;
    }

    private static bool IsValidSegmentChar(char ch) =>
        (ch >= 'a' && ch <= 'z')
        || (ch >= '0' && ch <= '9')
        || ch == '-'
        || ch == '_';
}
