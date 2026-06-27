namespace Cortex.Contained.Agent.Host.Coding;

/// <summary>
/// Agent-Host-side options for the external-agent relay.
/// </summary>
public sealed class CodingAgentOptions
{
    /// <summary>Hours of idleness after which the sweeper auto-ends a session. Default 6.</summary>
    public int IdleHours { get; set; } = 6;

    /// <summary>Maximum concurrent sessions across the guild. Default 3 (must match Bridge config).</summary>
    public int MaxSessions { get; set; } = 3;

    /// <summary>
    /// Ceiling (seconds) for any single Agent→Bridge coding invoke. Backstops an
    /// unresponsive Bridge so a stuck coding call cannot hold the per-channel lock.
    /// Default 45 — comfortably above the Bridge's 30s start timeout (so the Bridge's
    /// specific failure always wins) and well under the ~2-3 min comms bound. No coding call
    /// blocks on actual work; long-running work is observed via push events, not a long invoke.
    /// </summary>
    public int BridgeInvokeTimeoutSeconds { get; set; } = 45;
}
