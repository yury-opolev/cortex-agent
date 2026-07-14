namespace Cortex.Contained.Bridge.Mcp;

/// <summary>Pure allow-list policy for MCP tools. An empty allow-list exposes every tool.</summary>
public static class McpToolFilter
{
    /// <summary>
    /// True when <paramref name="toolName"/> is exposed: either the <paramref name="allowList"/>
    /// is empty (all tools), or it contains the tool name (ordinal match — MCP names are case-sensitive).
    /// </summary>
    public static bool IsAllowed(string toolName, IReadOnlyCollection<string> allowList)
    {
        ArgumentNullException.ThrowIfNull(allowList);

        if (allowList.Count == 0)
        {
            return true;
        }

        foreach (var allowed in allowList)
        {
            if (string.Equals(allowed, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="toolName"/> is classified as a mutation by the EXPLICIT admin
    /// <paramref name="mutationAllowList"/> (ordinal match — MCP names are case-sensitive).
    /// Deliberately the opposite default to <see cref="IsAllowed"/>: an empty list classifies
    /// NOTHING as a mutation. Classification is admin policy only — never inferred from tool
    /// names, descriptions, or untrusted MCP annotations.
    /// </summary>
    public static bool IsMutation(string toolName, IReadOnlyCollection<string> mutationAllowList)
    {
        ArgumentNullException.ThrowIfNull(mutationAllowList);

        foreach (var mutating in mutationAllowList)
        {
            if (string.Equals(mutating, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
