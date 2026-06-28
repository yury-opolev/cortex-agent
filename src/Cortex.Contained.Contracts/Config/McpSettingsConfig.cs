namespace Cortex.Contained.Contracts.Config;

/// <summary>
/// MCP plugin-system settings. The master <see cref="Enabled"/> switch plus the configured
/// servers. With zero servers the agent's tool list is unchanged.
/// </summary>
public sealed class McpSettingsConfig
{
    /// <summary>Master MCP switch (default true). When false, all MCP tools are removed live.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Configured MCP servers.</summary>
    public List<McpServerConfig> Servers { get; set; } = [];
}
