using System.Text.RegularExpressions;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Pure helpers for the agent-facing MCP tool namespacing scheme
/// <c>mcp__{serverKey}__{toolName}</c>. Server keys are validated to a safe,
/// collision-resistant character set; the <c>__</c> separator disambiguates the prefix.
/// </summary>
public static partial class McpToolNamer
{
    /// <summary>The namespacing prefix every MCP tool name carries.</summary>
    public const string Prefix = "mcp__";

    private const string Separator = "__";

    /// <summary>
    /// Builds the namespaced agent-facing tool name. The <paramref name="serverKey"/> is
    /// lowercased then validated to <c>[a-z0-9_-]+</c> (no <c>__</c> run); throws on an
    /// invalid key or empty tool name.
    /// </summary>
    public static string Full(string serverKey, string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var normalized = serverKey.ToLowerInvariant();
        if (!IsValidServerKey(normalized))
        {
            throw new ArgumentException($"Invalid MCP server key '{serverKey}'. Allowed: [a-z0-9_-], no '__'.", nameof(serverKey));
        }

        return string.Concat(Prefix, normalized, Separator, toolName);
    }

    /// <summary>True when <paramref name="serverKey"/> matches <c>[a-z0-9_-]+</c> and contains no <c>__</c> run.</summary>
    public static bool IsValidServerKey(string? serverKey)
    {
        if (string.IsNullOrEmpty(serverKey))
        {
            return false;
        }

        if (serverKey.Contains(Separator, StringComparison.Ordinal))
        {
            return false;
        }

        return ServerKeyPattern().IsMatch(serverKey);
    }

    /// <summary>
    /// Parses a namespaced tool name into its server key and tool name. Returns false for
    /// any string that does not start with <c>mcp__</c> followed by a non-empty server key,
    /// the <c>__</c> separator, and a non-empty tool name.
    /// </summary>
    public static bool TryParse(string? fullName, out string serverKey, out string toolName)
    {
        serverKey = string.Empty;
        toolName = string.Empty;

        if (string.IsNullOrEmpty(fullName) || !fullName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = fullName[Prefix.Length..];
        var separatorIndex = remainder.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var server = remainder[..separatorIndex];
        var tool = remainder[(separatorIndex + Separator.Length)..];
        if (server.Length == 0 || tool.Length == 0)
        {
            return false;
        }

        serverKey = server;
        toolName = tool;
        return true;
    }

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerKeyPattern();
}
