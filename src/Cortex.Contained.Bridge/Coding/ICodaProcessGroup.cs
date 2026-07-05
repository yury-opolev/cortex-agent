using System.Diagnostics;

namespace Cortex.Contained.Bridge.Coding;

/// <summary>
/// Owns the group that spawned coda processes are enrolled in so they cannot outlive the Bridge.
/// The Windows implementation (<see cref="WindowsJobProcessGroup"/>) uses a Job Object with
/// <c>KILL_ON_JOB_CLOSE</c>: when the Bridge process exits — gracefully, on a crash, or when
/// force-killed — the OS terminates every enrolled coda process. This complements the per-session
/// reactive reap (kill on crash) by covering the "Bridge died mid-session" case, which is how
/// orphaned <c>coda serve</c> processes accumulated across restarts.
/// </summary>
public interface ICodaProcessGroup
{
    /// <summary>
    /// Enroll a freshly-started coda process so it is force-terminated when the Bridge exits.
    /// Best-effort: a failure to enroll is logged, not thrown — the reactive reap still covers
    /// crashes during the Bridge's lifetime.
    /// </summary>
    void Register(Process process);
}
