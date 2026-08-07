using Cortex.Contained.Bridge.Setup;
using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Connectors;

/// <summary>
/// Reads and persists the connector settings block of <c>cortex.yml</c>. Holds the live
/// <see cref="BridgeConfig.Connectors"/> in memory and writes the whole config back to disk through
/// <c>BridgeSettingsWriter</c>. Nothing secret is written — connector tokens live only in DPAPI.
/// </summary>
public sealed partial class ConnectorConfigStore
{
    private readonly BridgeConfig config;
    private readonly string yamlPath;
    private readonly ILogger<ConnectorConfigStore> logger;

    /// <summary>Initialises a new <see cref="ConnectorConfigStore"/>.</summary>
    /// <param name="config">The live Bridge configuration.</param>
    /// <param name="yamlPath">Absolute path of <c>cortex.yml</c>.</param>
    /// <param name="logger">Logger for persistence events.</param>
    public ConnectorConfigStore(BridgeConfig config, string yamlPath, ILogger<ConnectorConfigStore> logger)
    {
        this.config = config;
        this.yamlPath = yamlPath;
        this.logger = logger;
    }

    /// <summary>The current in-memory connector settings.</summary>
    /// <returns>The live <see cref="ConnectorSettingsConfig"/> instance.</returns>
    public ConnectorSettingsConfig GetSettings() => this.config.Connectors;

    /// <summary>
    /// Replaces the in-memory connector settings with <paramref name="settings"/> and persists the
    /// whole config to YAML.
    /// </summary>
    /// <param name="settings">The settings to store and persist.</param>
    public void Save(ConnectorSettingsConfig settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.config.Connectors = settings;
        this.Persist();
    }

    /// <summary>Persists the current config to YAML.</summary>
    public void Persist()
    {
        BridgeSettingsWriter.PersistSettingsToYaml(this.config, this.yamlPath);
        this.LogPersisted();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connector settings persisted")]
    private partial void LogPersisted();
}
