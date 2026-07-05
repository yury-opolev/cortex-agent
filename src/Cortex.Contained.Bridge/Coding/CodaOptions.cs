namespace Cortex.Contained.Bridge.Coding;

/// <summary>YAML-bound options for the coda serve backend.</summary>
public sealed class CodaOptions
{
    /// <summary>Path to the coda binary. Default relies on PATH; Plan 4 points this at the bundled exe.</summary>
    public string CodaBinaryPath { get; set; } = "coda";

    public int MaxSessions { get; set; } = 3;

    public int IdleHours { get; set; } = 6;

    /// <summary>
    /// Seconds to wait for the coda <c>initialize</c> handshake before giving up. Default 30.
    /// Startup is spawn + handshake only (no LLM work), so a bad config should fail fast — and
    /// this must stay below the Agent→Bridge invoke ceiling so the Bridge's specific failure wins.
    /// </summary>
    public int StartTimeoutSeconds { get; set; } = 30;

    /// <summary>Seconds to wait for control RPCs (interrupt/shutdown/status/history). Default 15.</summary>
    public int ControlTimeoutSeconds { get; set; } = 15;

    /// <summary>Seconds of no coda activity during a running turn before declaring it frozen. Default 300.</summary>
    public int PromptIdleTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// How a spawned coda session sources its MCP servers. Default <see cref="CodaMcpPolicy.Host"/>
    /// (coda uses the host's own <c>~/.coda/.mcp.json</c>). See <see cref="CuratedMcpDir"/> for
    /// <see cref="CodaMcpPolicy.Curated"/>.
    /// </summary>
    public CodaMcpPolicy Mcp { get; set; } = CodaMcpPolicy.Host;

    /// <summary>
    /// Directory holding the orchestrator-curated <c>.mcp.json</c>, used only when
    /// <see cref="Mcp"/> is <see cref="CodaMcpPolicy.Curated"/>. Exported to the spawned coda as
    /// <c>CODA_USER_MCP_DIR</c>, which replaces only the <b>user</b> layer — a project-level
    /// <c>&lt;cwd&gt;/.mcp.json</c> still loads and overrides it (see <see cref="CodaMcpPolicy.Curated"/>).
    /// When blank, curated mode degrades to host behavior.
    /// </summary>
    public string? CuratedMcpDir { get; set; }

    /// <summary>Returns a shallow copy of these options — so callers that override a couple of
    /// fields (e.g. the MCP policy) never silently drop the rest. Add new fields here.</summary>
    public CodaOptions Clone() => new()
    {
        CodaBinaryPath = this.CodaBinaryPath,
        MaxSessions = this.MaxSessions,
        IdleHours = this.IdleHours,
        StartTimeoutSeconds = this.StartTimeoutSeconds,
        ControlTimeoutSeconds = this.ControlTimeoutSeconds,
        PromptIdleTimeoutSeconds = this.PromptIdleTimeoutSeconds,
        Mcp = this.Mcp,
        CuratedMcpDir = this.CuratedMcpDir,
    };

    /// <summary>
    /// Returns the best coda binary path to use at runtime.
    /// If a bundled <c>coda/coda.exe</c> exists next to the application, that path is returned.
    /// Otherwise returns <c>"coda"</c> so the OS PATH is used (useful in dev).
    /// </summary>
    public static string ResolveDefaultBinaryPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "coda", "coda.exe");
        return File.Exists(bundled) ? bundled : "coda";
    }
}
