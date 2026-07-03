namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Controls how a Cortex-spawned <c>coda serve</c> session sources its MCP servers.
/// </summary>
public enum CodaMcpPolicy
{
    /// <summary>Default: coda uses the host's own MCP config (<c>~/.coda/.mcp.json</c> + <c>&lt;cwd&gt;/.mcp.json</c>).</summary>
    Host = 0,

    /// <summary>
    /// coda uses an orchestrator-curated config: <c>CODA_USER_MCP_DIR</c> is pointed at
    /// <see cref="CodaOptions.CuratedMcpDir"/> so the coding engine sees only a vetted MCP set,
    /// not the operator's personal servers.
    /// </summary>
    Curated = 1,

    /// <summary>coda connects no MCP servers (<c>--no-mcp</c>).</summary>
    Off = 2,
}
