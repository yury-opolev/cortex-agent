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
}
