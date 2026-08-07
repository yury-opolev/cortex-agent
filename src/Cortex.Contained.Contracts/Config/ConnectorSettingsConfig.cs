namespace Cortex.Contained.Contracts.Config;

/// <summary>Connector plugin-system settings.</summary>
public sealed class ConnectorSettingsConfig
{
    /// <summary>Master connector switch (default true). When false, all plugin channels are dropped live.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When true, connectors must be approved through the pairing flow before they are accepted.</summary>
    public bool RequireApproval { get; set; } = true;

    /// <summary>Maximum number of concurrently registered connectors.</summary>
    public int MaxConnectors { get; set; } = 16;

    /// <summary>Replay settings applied when a connector attaches after being offline.</summary>
    public ConnectorReplayConfig Replay { get; set; } = new();

    /// <summary>Per-connector frame and rate limits.</summary>
    public ConnectorLimitsConfig Limits { get; set; } = new();
}
