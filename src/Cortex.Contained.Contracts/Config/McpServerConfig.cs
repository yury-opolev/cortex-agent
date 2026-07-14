namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// Configuration for a single MCP server. Lives in the <c>mcp.servers</c> block of
/// <c>cortex.yml</c>. Holds only non-secret fields — secret values are referenced by
/// <see cref="SecretRef"/> (a DPAPI key id), never stored inline.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Unique, lowercased server key (<c>[a-z0-9_-]+</c>); used in the tool prefix <c>mcp__{Key}__*</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Whether this server is enabled (gated by the master <see cref="McpSettingsConfig.Enabled"/>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Transport used to reach the server.</summary>
    public McpTransport Transport { get; set; }

    /// <summary>HTTP endpoint URL (http transport only).</summary>
    public string? Url { get; set; }

    /// <summary>Executable/command to spawn (stdio transport only).</summary>
    public string? Command { get; set; }

    /// <summary>Command arguments (stdio transport only).</summary>
    public List<string> Args { get; set; } = [];

    /// <summary>
    /// Environment variables injected into the spawned process (stdio transport only).
    /// A value may be a secret-reference token (<c>${secret:id}</c>) resolved from DPAPI at spawn.
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = [];

    /// <summary>Authentication mode.</summary>
    public McpAuthMode Auth { get; set; } = McpAuthMode.Auto;

    /// <summary>Custom header name for an apiKey over http (default <c>Authorization: Bearer</c>).</summary>
    public string? ApiKeyHeader { get; set; }

    /// <summary>DPAPI key id of the static api key (value never stored in yaml).</summary>
    public string? SecretRef { get; set; }

    /// <summary>Allowed tool names; empty = all tools exposed.</summary>
    public List<string> ToolAllowList { get; set; } = [];

    /// <summary>
    /// Tool names the administrator EXPLICITLY classified as mutating (state-changing). This is
    /// admin policy only — never inferred from tool names, descriptions, or untrusted MCP
    /// annotations. A mutation tool surfaces <c>RequiresApproval=true</c> in the agent catalog and
    /// is refused by the direct invocation path; empty = no tool is classified as a mutation.
    /// Names are normalized exactly like <see cref="ToolAllowList"/>, and when
    /// <see cref="ToolAllowList"/> is non-empty every mutation tool must also be present there.
    /// </summary>
    public List<string> MutationToolAllowList { get; set; } = [];

    /// <summary>Minimum valid <see cref="CallTimeoutSeconds"/> (must be positive).</summary>
    public const int MinCallTimeoutSeconds = 1;

    /// <summary>
    /// Maximum valid <see cref="CallTimeoutSeconds"/>. Deliberately kept BELOW the Agent-side
    /// gateway ceiling (60s — see <c>SignalRMcpGateway.TimeoutCeilingSeconds</c>) so the Bridge's
    /// own per-call bound always resolves first: the Agent should see a definitive Bridge timeout
    /// result rather than its own gateway timing out first and reporting an ambiguous outcome.
    /// </summary>
    public const int MaxCallTimeoutSeconds = 59;

    /// <summary>Default <see cref="CallTimeoutSeconds"/> when a server config does not override it.</summary>
    public const int DefaultCallTimeoutSeconds = 45;

    /// <summary>
    /// How long the Bridge waits for a single <c>tools/call</c> to complete before treating it as
    /// an ambiguous, never-retried timeout (<c>McpFailureKind.Timeout</c> / <c>OutcomeUnknown</c>).
    /// Out-of-range values are REJECTED at the config-edit boundary — never silently clamped. Must
    /// stay within [<see cref="MinCallTimeoutSeconds"/>, <see cref="MaxCallTimeoutSeconds"/>].
    /// </summary>
    public int CallTimeoutSeconds { get; set; } = DefaultCallTimeoutSeconds;

    /// <summary>Minimum valid <see cref="MaxResultBytes"/> (must be positive).</summary>
    public const int MinMaxResultBytes = 1;

    /// <summary>
    /// Maximum UTF-8 byte size of a flattened MCP tool result before it is truncated with a
    /// deterministic marker. Bounds what a single MCP call can push back across the Bridge→Agent
    /// SignalR hub. Out-of-range values are REJECTED at the config-edit boundary — never silently
    /// clamped. Must be at least <see cref="MinMaxResultBytes"/>.
    /// </summary>
    public int MaxResultBytes { get; set; } = 50 * 1024;
}
