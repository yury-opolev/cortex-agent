namespace Cortex.Contained.Bridge.Control;

/// <summary>
/// Tiny piece of shared state between the <c>POST /api/control/restart-bridge</c>
/// endpoint and the <c>IHostApplicationLifetime.ApplicationStopped</c> callback.
/// The endpoint flips <see cref="RequestRestart"/> and then asks the host to
/// stop; the lifetime callback reads <see cref="IsRestartRequested"/> and either
/// returns control normally (exit 0) or calls <c>Environment.Exit(73)</c> so the
/// Launcher respawns the Bridge.
/// </summary>
/// <remarks>
/// Registered as a singleton in DI. Thread-safe — flag is accessed via
/// <see cref="Interlocked"/> / <see cref="Volatile"/> so the endpoint thread and
/// the lifetime callback (which runs on whatever thread fires
/// <c>ApplicationStopped</c>) agree on its value.
/// </remarks>
public sealed class RestartCoordinator
{
    /// <summary>
    /// Exit code emitted by the Bridge when it is shutting down to be respawned.
    /// The Launcher's <c>BridgeProcessService.OnExited</c> consumer matches on
    /// exactly this value to decide between Restart vs. Error.
    /// </summary>
    public const int RestartExitCode = 73;

    private int requested;

    /// <summary>True once <see cref="RequestRestart"/> has been called at least once.</summary>
    public bool IsRestartRequested => Volatile.Read(ref this.requested) == 1;

    /// <summary>
    /// Mark the upcoming shutdown as a Web-UI-initiated restart. Idempotent —
    /// repeated calls are a no-op.
    /// </summary>
    public void RequestRestart() => Interlocked.Exchange(ref this.requested, 1);

    /// <summary>
    /// Exit code the process should terminate with: <see cref="RestartExitCode"/>
    /// if a restart was requested, otherwise 0 (clean stop, no respawn).
    /// </summary>
    public int ResolveExitCode() => this.IsRestartRequested ? RestartExitCode : 0;
}
