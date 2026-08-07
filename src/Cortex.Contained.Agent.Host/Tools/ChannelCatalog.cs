using Cortex.Contained.Contracts.Channels;

namespace Cortex.Contained.Agent.Host.Tools;

/// <summary>
/// One known channel: its canonical id, the input aliases that resolve to it, and the
/// various human-facing names (short friendly name, title-cased display name, and the
/// optional system-prompt label).
/// </summary>
internal sealed record ChannelDescriptor(
    string CanonicalId,
    string FriendlyName,
    string DisplayName,
    string? PromptLabel,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Single source of truth for the known channels. Adding a channel = adding ONE entry
/// here; previously the same channel set was hand-maintained across six separate tables
/// (the resolve switch, the alias array, the valid-names list, ToFriendlyName,
/// ToDisplayName, and PromptAssembler's label switch).
///
/// Plugin (connector) channels are unbounded and dynamic, so they are NOT in
/// <see cref="All"/>. Instead, <see cref="ResolveCanonical"/> and
/// <see cref="ByCanonicalId"/> synthesise descriptors on demand for any id that
/// passes <see cref="PluginChannelId.IsPluginChannelId"/>.
/// </summary>
internal static class ChannelCatalog
{
    /// <summary>All known first-party channels, in canonical declaration order.</summary>
    public static readonly IReadOnlyList<ChannelDescriptor> All =
    [
        new("discord-dm", FriendlyName: "discord", DisplayName: "Discord",
            PromptLabel: "Discord (direct message)", Aliases: ["discord", "discord-dm"]),
        new("discord-guild", FriendlyName: "discord-guild", DisplayName: "Discord (guild)",
            PromptLabel: "Discord (server channel)", Aliases: ["discord-guild"]),
        new("discord-voice", FriendlyName: "discord-voice", DisplayName: "Discord Voice",
            PromptLabel: null, Aliases: ["discord-voice"]),
        new("webchat-default", FriendlyName: "webchat", DisplayName: "WebChat",
            PromptLabel: "the web chat interface", Aliases: ["webchat", "webchat-default"]),
        new("voice-default", FriendlyName: "voice", DisplayName: "Voice",
            PromptLabel: "the voice channel", Aliases: ["voice", "voice-default"]),
    ];

    private static readonly Dictionary<string, ChannelDescriptor> byCanonicalId =
        All.ToDictionary(channel => channel.CanonicalId, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> aliasToCanonical =
        All.SelectMany(channel => channel.Aliases.Select(alias => (Alias: alias, channel.CanonicalId)))
           .ToDictionary(pair => pair.Alias, pair => pair.CanonicalId, StringComparer.OrdinalIgnoreCase);

    /// <summary>(alias, canonicalId) pairs in declaration order.</summary>
    public static IEnumerable<(string Alias, string CanonicalId)> AliasPairs =>
        All.SelectMany(channel => channel.Aliases.Select(alias => (alias, channel.CanonicalId)));

    /// <summary>
    /// Resolve an input alias (case-insensitive) to a canonical id, or null if unknown.
    /// A valid plugin channel id (e.g. <c>plugin:terminal:default</c>) is its own
    /// canonical id and is returned unchanged.
    /// </summary>
    public static string? ResolveCanonical(string alias)
    {
        if (aliasToCanonical.TryGetValue(alias, out var canonicalId))
        {
            return canonicalId;
        }

        if (PluginChannelId.IsPluginChannelId(alias))
        {
            return alias;
        }

        return null;
    }

    /// <summary>
    /// Look up a channel by its canonical id, or null if unknown.
    /// For a valid plugin channel id a descriptor is synthesised on demand —
    /// plugin channels are not in <see cref="All"/> because they are unbounded.
    /// </summary>
    public static ChannelDescriptor? ByCanonicalId(string? canonicalId)
    {
        if (canonicalId is null)
        {
            return null;
        }

        if (byCanonicalId.TryGetValue(canonicalId, out var descriptor))
        {
            return descriptor;
        }

        if (PluginChannelId.TryParse(canonicalId, out var key, out _))
        {
            return new ChannelDescriptor(
                CanonicalId: canonicalId,
                FriendlyName: canonicalId,
                DisplayName: $"Connector ({key})",
                PromptLabel: $"the {key} connector",
                Aliases: [canonicalId]);
        }

        return null;
    }
}
