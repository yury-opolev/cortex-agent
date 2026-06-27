using System.Collections.Frozen;

namespace Cortex.Contained.Channels.Discord;

/// <summary>Built-in backchannel vocabulary. Not user-configurable.</summary>
internal static class BackchannelLexicon
{
    private static readonly FrozenSet<string> tokens = new[]
    {
        "mhm", "mm", "mmhm", "uh", "huh", "uhhuh", "yeah", "yep", "yup",
        "ok", "okay", "right", "sure", "aha", "ah", "oh", "hm", "hmm",
        "haha", "hehe", "lol", "nice", "cool", "gotcha", "true",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when every token of <paramref name="text"/> is a backchannel token.</summary>
    public static bool IsBackchannelOnly(string text)
    {
        var words = text.Split(
            [' ', '\t', '\n', ',', '.', '!', '?'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return false;
        }

        foreach (var w in words)
        {
            if (!tokens.Contains(w))
            {
                return false;
            }
        }

        return true;
    }

    public static int WordCount(string text) => text.Split(
        [' ', '\t', '\n', ',', '.', '!', '?'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
}
