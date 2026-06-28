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
}
