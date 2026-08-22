using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Watchdog that restarts the uni-voices TTS sidecar when it is reachable but every
/// synthesis fails, so voice recovers on its own instead of staying silently dead.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the CUDA context inside the sidecar process can be poisoned by a
/// host GPU driver reset or VRAM exhaustion. That fault is <em>sticky</em> — once the
/// context is dead, every later CUDA call in that process raises
/// "CUDA error: unknown error", so the sidecar can never recover in-process and every
/// sentence returns HTTP 500.
/// </para>
/// <para>
/// Nothing else notices: uni-voices' <c>/health</c> reports <c>loaded: true</c> for an
/// engine that merely built successfully once, so the readiness probe, the container
/// healthcheck and <see cref="RemoteTtsProvider.IsReady"/> all stay green while the user
/// only ever hears the pre-baked "trouble speaking" notice. Restarting the container is
/// the only reliable remedy because it is the only way to get a fresh CUDA context.
/// </para>
/// <para>
/// Faults are counted across engines (the poisoned context is process-wide, so kokoro,
/// roest-da and silero all fail together) and a cooldown stops a genuinely broken
/// sidecar from being restarted in a loop.
/// </para>
/// </remarks>
public sealed partial class UniVoicesFaultRecovery : ITtsFaultListener
{
    /// <summary>Consecutive sidecar-side failures that trigger a restart.</summary>
    internal const int FaultThreshold = 3;

    /// <summary>Minimum gap between two restarts.</summary>
    internal static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(5);

    private readonly IComposeCommandRunner runner;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<UniVoicesFaultRecovery> logger;

    // Admits one restart attempt at a time; extra faults arriving mid-restart are dropped.
    // 0 = idle, 1 = a restart is in flight.
    private int recovering;

    private int consecutiveFaults;
    private DateTimeOffset? lastRestart;

    public UniVoicesFaultRecovery(
        IComposeCommandRunner runner,
        TimeProvider timeProvider,
        ILogger<UniVoicesFaultRecovery> logger)
    {
        this.runner = runner;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>Current fault streak. Test seam.</summary>
    internal int ConsecutiveFaults => Volatile.Read(ref this.consecutiveFaults);

    /// <summary>The most recently kicked-off recovery task, if any. Test seam.</summary>
    internal Task? PendingRecovery { get; private set; }

    /// <inheritdoc />
    public void OnSynthesisSuccess(string engineName)
        => Interlocked.Exchange(ref this.consecutiveFaults, 0);

    /// <inheritdoc />
    public void OnSynthesisFault(string engineName, int statusCode)
    {
        // Only server-side faults indicate a sick sidecar. A 4xx means the Bridge asked
        // for an engine/voice the sidecar can't serve — restarting would hide our bug.
        if (statusCode is < 500 or >= 600)
        {
            return;
        }

        if (Interlocked.Increment(ref this.consecutiveFaults) < FaultThreshold)
        {
            return;
        }

        // Fire-and-forget: this runs on the synthesis hot path. TryRecoverAsync never
        // throws, so the task can't fault unobserved.
        this.PendingRecovery = Task.Run(() => this.TryRecoverAsync(CancellationToken.None));
    }

    /// <summary>
    /// Restarts the sidecar when the fault threshold is met and the cooldown has elapsed.
    /// Returns true only when a restart actually succeeded. Never throws.
    /// </summary>
    internal async Task<bool> TryRecoverAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref this.recovering, 1, 0) != 0)
        {
            return false; // a restart is already in flight
        }

        try
        {
            var faults = Volatile.Read(ref this.consecutiveFaults);
            if (faults < FaultThreshold)
            {
                return false;
            }

            var now = this.timeProvider.GetUtcNow();
            if (this.lastRestart is { } previous && now - previous < RestartCooldown)
            {
                this.LogRestartSuppressed((int)(now - previous).TotalSeconds);
                return false;
            }

            this.LogRestarting(faults);
            this.lastRestart = now;

            var restarted = await this.runner.RestartDanishAsync(cancellationToken).ConfigureAwait(false);

            // Reset either way: on success the streak is stale, and on failure the
            // cooldown — not the counter — governs the next attempt.
            Interlocked.Exchange(ref this.consecutiveFaults, 0);

            if (restarted)
            {
                this.LogRestarted();
            }
            else
            {
                this.LogRestartFailed();
            }

            return restarted;
        }
        catch (Exception ex)
        {
            this.LogRecoveryError(ex.Message);
            return false;
        }
        finally
        {
            Volatile.Write(ref this.recovering, 0);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "uni-voices failed {Faults} consecutive syntheses while reporting healthy — restarting the sidecar to clear a poisoned GPU context")]
    private partial void LogRestarting(int faults);

    [LoggerMessage(Level = LogLevel.Information, Message = "uni-voices sidecar restarted — TTS should recover")]
    private partial void LogRestarted();

    [LoggerMessage(Level = LogLevel.Error, Message = "uni-voices sidecar restart failed — voice stays degraded")]
    private partial void LogRestartFailed();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "uni-voices still failing but last restart was {ElapsedSeconds}s ago — suppressed by cooldown")]
    private partial void LogRestartSuppressed(int elapsedSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "uni-voices fault recovery error: {Error}")]
    private partial void LogRecoveryError(string error);
}
