using System.Diagnostics.CodeAnalysis;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// Resolves friendly channel names (used by tools) to canonical channel IDs.
/// Maps shorthand names like "discord" to their canonical form ("discord-dm"),
/// and validates that a channel name is recognized.
/// Can optionally filter against a list of active channels from the Bridge.
/// </summary>
internal static class ChannelNameResolver
{
    /// <summary>
    /// Maps a user-supplied channel name to a canonical channel ID.
    /// </summary>
    /// <returns>The resolved channel ID, or null if unrecognized.</returns>
    public static string? Resolve(string channelName) => ChannelCatalog.ResolveCanonical(channelName);

    /// <summary>
    /// Tries to resolve a channel name. Returns true on success.
    /// </summary>
    public static bool TryResolve(string channelName, [NotNullWhen(true)] out string? channelId)
    {
        channelId = Resolve(channelName);
        return channelId is not null;
    }

    /// <summary>
    /// Returns a comma-separated list of valid channel names for error messages.
    /// If <paramref name="activeChannelIds"/> is provided, only includes channels
    /// that are currently active on the Bridge.
    /// </summary>
    public static string GetValidChannelNames(IReadOnlyList<string>? activeChannelIds = null)
    {
        if (activeChannelIds is null || activeChannelIds.Count == 0)
        {
            return ValidChannelNames;
        }

        // Build friendly names for active channels only
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (alias, canonicalId) in ChannelCatalog.AliasPairs)
        {
            if (activeChannelIds.Contains(canonicalId) && seen.Add(alias))
            {
                names.Add(alias);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : ValidChannelNames;
    }

    /// <summary>
    /// Returns a comma-separated list of valid channel names for error messages.
    /// </summary>
    public static string ValidChannelNames => "webchat, discord, discord-dm, discord-guild, discord-voice, voice";

    /// <summary>
    /// Maps a canonical channel ID back to a short friendly name for error messages.
    /// Returns null for unknown or synthetic channel IDs (e.g. "scheduled").
    /// </summary>
    public static string? ToFriendlyName(string? channelId) => ChannelCatalog.ByCanonicalId(channelId)?.FriendlyName;

    /// <summary>
    /// Maps a canonical channel ID to a title-cased display name suitable for
    /// user-facing UI strings (e.g. <c>"webchat-default"</c> → <c>"WebChat"</c>).
    /// Returns the canonical id verbatim for unknown channels so callers can decide
    /// whether to surface raw ids or substitute their own fallback.
    /// </summary>
    public static string ToDisplayName(string channelId) => ChannelCatalog.ByCanonicalId(channelId)?.DisplayName ?? channelId;

    /// <summary>
    /// Checks if a canonical channel ID is in the active channels list.
    /// Returns true if active channels list is empty (not yet received from Bridge).
    /// </summary>
    public static bool IsChannelActive(string channelId, IReadOnlyList<string> activeChannels)
    {
        // If no active channels list has been received yet, allow all channels
        // (graceful degradation — Bridge hasn't called SetActiveChannels yet)
        if (activeChannels.Count == 0)
        {
            return true;
        }

        return activeChannels.Contains(channelId);
    }
}
