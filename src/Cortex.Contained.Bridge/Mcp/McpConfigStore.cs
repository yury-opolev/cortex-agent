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
        this.WarnOnPlaintextEnvSecrets();
        this.WarnOnDuplicateKeys();
        BridgeSettingsWriter.PersistSettingsToYaml(this.config, this.yamlPath);
        this.LogPersisted(this.config.Mcp.Servers.Count);
    }

    /// <summary>
    /// Logs a warning when two servers share a key (e.g. a hand-edited config): the reconcile would
    /// silently keep only one, so surface it. The Web-UI add path already blocks duplicates up front.
    /// </summary>
    private void WarnOnDuplicateKeys()
    {
        var duplicate = McpServerRequestMapper.FindDuplicateKey(this.config.Mcp.Servers.Select(s => s.Key));
        if (duplicate is not null)
        {
            this.LogDuplicateKey(duplicate);
        }
    }

    /// <summary>
    /// Logs a warning (never blocks) when a stdio <c>env</c> value looks like a literal secret rather
    /// than a <c>${secret:id}</c> token, so plaintext secrets in YAML are discouraged. The value
    /// itself is never logged — only the server key and env-var name.
    /// </summary>
    private void WarnOnPlaintextEnvSecrets()
    {
        foreach (var server in this.config.Mcp.Servers)
        {
            foreach (var (name, value) in server.Env)
            {
                if (McpEnvSecretHeuristic.LooksLikeSecret(value))
                {
                    this.LogPlaintextEnvSecret(server.Key, name);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "MCP settings persisted: {ServerCount} servers")]
    private partial void LogPersisted(int serverCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MCP config has a duplicate server key '{ServerKey}' — only one will run; remove the duplicate")]
    private partial void LogDuplicateKey(string serverKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MCP server '{ServerKey}' env '{EnvName}' looks like a plaintext secret — prefer a ${{secret:id}} reference so it is stored encrypted (DPAPI), not in YAML")]
    private partial void LogPlaintextEnvSecret(string serverKey, string envName);
}
