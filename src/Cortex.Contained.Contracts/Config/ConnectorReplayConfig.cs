namespace Cortex.Contained.Contracts.Config;

/// <summary>Replay settings applied when a connector attaches after being offline.</summary>
public sealed class ConnectorReplayConfig
{
    /// <summary>Maximum number of messages replayed on attach.</summary>
    public int MaxMessages { get; set; } = 100;

    /// <summary>Maximum age of messages eligible for replay.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);
}
