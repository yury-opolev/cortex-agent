namespace Cortex.Contained.Launcher;

/// <summary>
/// Represents the overall state of the Cortex system as managed by the Launcher.
/// </summary>
public enum CortexState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}
