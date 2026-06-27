using Cortex.Contained.Contracts.Recording;

namespace Cortex.Contained.Channels.Discord.Recording;

/// <summary>
/// Outcome of resolving the user's <c>channel:</c> slash-command argument.
/// </summary>
public abstract record ResolveResult
{
    public sealed record Resolved(string ChannelKey, string Display) : ResolveResult;

    public sealed record FallbackToCurrent() : ResolveResult;

    public sealed record NotFound(string Name) : ResolveResult;

    public sealed record Ambiguous(string Name) : ResolveResult;
}

/// <summary>
/// Pure resolver for the <c>channel:</c> parameter of <c>/voice-record</c>:
/// <list type="bullet">
///   <item>empty/null → <see cref="ResolveResult.FallbackToCurrent"/> (use the user's current voice channel)</item>
///   <item><c>"host"</c> (case-insensitive) → <see cref="ChannelKey.Host"/></item>
///   <item>unique name match in the guild → <see cref="ChannelKey.ForDiscord"/></item>
///   <item>no match → <see cref="ResolveResult.NotFound"/>; multiple matches → <see cref="ResolveResult.Ambiguous"/></item>
/// </list>
/// </summary>
public static class VoiceChannelNameResolver
{
    public const string HostName = "host";

    public static ResolveResult Resolve(string? input, IReadOnlyList<(string Name, ulong Id)> channels)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new ResolveResult.FallbackToCurrent();
        }

        var trimmed = input.Trim();
        if (string.Equals(trimmed, HostName, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolveResult.Resolved(ChannelKey.Host, ChannelKey.Host);
        }

        var matches = channels
            .Where(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return matches.Length switch
        {
            0 => new ResolveResult.NotFound(trimmed),
            1 => new ResolveResult.Resolved(ChannelKey.ForDiscord(matches[0].Id), matches[0].Name),
            _ => new ResolveResult.Ambiguous(trimmed),
        };
    }
}
