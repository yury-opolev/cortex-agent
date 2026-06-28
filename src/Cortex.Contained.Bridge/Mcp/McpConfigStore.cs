using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Mcp;

/// <summary>
/// Reads and persists the MCP settings block of <c>cortex.yml</c>. Holds the live
/// <see cref="BridgeConfig.Mcp"/> in memory and writes the whole config (non-secret) back to disk
/// through <see cref="BridgeSettingsWriter"/>; secret values live only in DPAPI.
/// </summary>
public sealed partial class McpConfigStore
{
    private readonly BridgeConfig config;
    private readonly string yamlPath;
    private readonly ILogger<McpConfigStore> logger;

    public McpConfigStore(BridgeConfig config, string yamlPath, ILogger<McpConfigStore> logger)
    {
        this.config = config;
        this.yamlPath = yamlPath;
        this.logger = logger;
    }

    /// <summary>The current in-memory MCP settings.</summary>
    public McpSettingsConfig GetSettings() => this.config.Mcp;

    /// <summary>
    /// Replaces the in-memory MCP settings with <paramref name="settings"/> and persists the whole
    /// config to YAML. Only non-secret fields are written.
    /// </summary>
    public void Save(McpSettingsConfig settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.config.Mcp = settings;
        this.Persist();
    }

    /// <summary>Persists the current config to YAML (non-secret only).</summary>
    public void Persist()
    {
        BridgeSettingsWriter.PersistSettingsToYaml(this.config, this.yamlPath);
        this.LogPersisted(this.config.Mcp.Servers.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP settings persisted: {ServerCount} servers")]
    private partial void LogPersisted(int serverCount);
}
