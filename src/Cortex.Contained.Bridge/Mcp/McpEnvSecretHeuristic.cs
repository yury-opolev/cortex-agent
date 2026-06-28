using Cortex.Contained.Bridge.Mcp.Auth;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure heuristic that flags a stdio <c>env</c> value that looks like a literal secret (a long,
/// high-entropy token) rather than a <c>${secret:id}</c> reference. Used only to emit a warning at
/// persist time — never to block — so plaintext secrets in YAML are discouraged in favour of the
/// DPAPI-backed secret store. Intentionally conservative to avoid false positives on ordinary config.
/// </summary>
public static class McpEnvSecretHeuristic
{
    private const int MinLength = 24;
    private const double EntropyThreshold = 3.5;

    /// <summary>
    /// True when <paramref name="value"/> appears to be a high-entropy literal secret (and is not a
    /// <c>${secret:id}</c> token, a URL, or whitespace-bearing config text).
    /// </summary>
    public static bool LooksLikeSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < MinLength)
        {
            return false;
        }

        // A proper secret reference is exactly what we want — never warn about it.
        if (McpSecretRef.TryParse(value, out _))
        {
            return false;
        }

        // URLs/endpoints are legitimately long but aren't secrets.
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Whitespace strongly implies human-readable config, not a token.
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return ShannonEntropyBitsPerChar(value) >= EntropyThreshold;
    }

    private static double ShannonEntropyBitsPerChar(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var ch in value)
        {
            counts[ch] = counts.GetValueOrDefault(ch) + 1;
        }

        var length = value.Length;
        var entropy = 0.0;
        foreach (var count in counts.Values)
        {
            var probability = (double)count / length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }
}
