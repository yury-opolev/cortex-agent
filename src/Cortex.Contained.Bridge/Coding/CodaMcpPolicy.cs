namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Controls how a Cortex-spawned <c>coda serve</c> session sources its MCP servers.
/// </summary>
public enum CodaMcpPolicy
{
    /// <summary>Default: coda uses the host's own MCP config (<c>~/.coda/.mcp.json</c> + <c>&lt;cwd&gt;/.mcp.json</c>).</summary>
    Host = 0,

    /// <summary>
    /// coda uses an orchestrator-curated config in <b>isolation</b>: <c>CODA_USER_MCP_DIR</c> is
    /// pointed at <see cref="CodaOptions.CuratedMcpDir"/> (the vetted set instead of the operator's
    /// personal <c>~/.coda</c>) <b>and</b> the serve process is spawned with <c>--no-project-mcp</c>,
    /// so a repo's <c>&lt;cwd&gt;/.mcp.json</c> cannot override or add to the curated set. The coding
    /// engine sees only the vetted servers. (Use <see cref="Off"/> for no external MCP at all.)
    /// </summary>
    Curated = 1,

    /// <summary>coda connects no MCP servers (<c>--no-mcp</c>).</summary>
    Off = 2,
}
