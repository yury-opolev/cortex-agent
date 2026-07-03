namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Resolves the extra process environment for a spawned <c>coda serve</c> session from its MCP
/// policy. <see cref="CodaMcpPolicy.Curated"/> exports <c>CODA_USER_MCP_DIR</c> so coda reads an
/// orchestrator-owned <c>.mcp.json</c> instead of the operator's personal <c>~/.coda</c>; every
/// other policy (and curated with a blank directory) adds nothing.
/// </summary>
public static class CodaMcpEnvironment
{
    /// <summary>The env var coda reads to override the user-level MCP config directory.</summary>
    public const string UserMcpDirVar = "CODA_USER_MCP_DIR";

    private static readonly IReadOnlyDictionary<string, string> emptyEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Curated + non-blank <paramref name="curatedMcpDir"/> → <c>{ CODA_USER_MCP_DIR = dir }</c>.
    /// Every other policy — and curated with a blank dir — returns empty, so coda falls back to its
    /// default host config rather than a half-set variable.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(CodaMcpPolicy mcp, string? curatedMcpDir)
    {
        if (mcp == CodaMcpPolicy.Curated && !string.IsNullOrWhiteSpace(curatedMcpDir))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [UserMcpDirVar] = curatedMcpDir.Trim(),
            };
        }

        return emptyEnvironment;
    }
}
