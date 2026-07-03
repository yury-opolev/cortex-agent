namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Controls how a Cortex-spawned <c>coda serve</c> session sources its MCP servers.
/// </summary>
public enum CodaMcpPolicy
{
    /// <summary>Default: coda uses the host's own MCP config (<c>~/.coda/.mcp.json</c> + <c>&lt;cwd&gt;/.mcp.json</c>).</summary>
    Host = 0,

    /// <summary>
    /// coda uses an orchestrator-curated <b>user-level</b> config: <c>CODA_USER_MCP_DIR</c> is
    /// pointed at <see cref="CodaOptions.CuratedMcpDir"/> so the coding engine gets the vetted MCP
    /// set instead of the operator's personal <c>~/.coda</c>.
    /// <para>
    /// CAVEAT (not full isolation): coda still loads <c>&lt;cwd&gt;/.mcp.json</c> from the session's
    /// working folder and it <b>overrides</b> the curated user layer. A repo that ships its own
    /// <c>.mcp.json</c> can therefore still introduce servers. Use <see cref="Off"/> for a hard
    /// guarantee that no external MCP servers load.
    /// </para>
    /// </summary>
    Curated = 1,

    /// <summary>coda connects no MCP servers (<c>--no-mcp</c>).</summary>
    Off = 2,
}
