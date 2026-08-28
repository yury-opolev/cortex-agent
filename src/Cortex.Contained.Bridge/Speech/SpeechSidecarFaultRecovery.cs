using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Watchdog that restarts a GPU speech sidecar when it is reachable but every request
/// fails, so voice recovers on its own instead of staying silently dead.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the CUDA context inside a long-running sidecar process can be
/// poisoned by a host GPU driver reset or VRAM exhaustion. That fault is <em>sticky</em>
/// — once the context is dead, every later CUDA call in that process raises
/// "CUDA error: unknown error", so the sidecar can never recover in-process and every
/// request returns HTTP 500.
/// </para>
/// <para>
/// Nothing else notices, because both sidecars keep reporting themselves healthy:
/// uni-voices' <c>/health</c> reports <c>loaded: true</c> for an engine that merely built
/// successfully once, and whisper-stt's reports <c>{"loaded":true,"healthy":true}</c>.
/// So the container healthchecks, the readiness probes and the clients' <c>IsReady</c>
/// all stay green while the agent is silently deaf (STT) or mute (TTS). Restarting the
/// container is the only reliable remedy, because it is the only way to get a fresh
/// CUDA context.
/// </para>
/// <para>
/// State is tracked per sidecar: TTS and STT run in separate processes and are poisoned
/// independently, so a dead uni-voices must not trigger a whisper-stt restart. Within
/// the TTS sidecar, faults from all engines share one counter because the poisoned
/// context is process-wide.
/// </para>
/// </remarks>
public sealed partial class SpeechSidecarFaultRecovery : ISpeechSidecarFaultListener
{
    /// <summary>Consecutive sidecar-side failures that trigger a restart.</summary>
    internal const int FaultThreshold = 3;

    /// <summary>Minimum gap between two restarts of the same sidecar.</summary>
    internal static readonly TimeSpan RestartCooldown = TimeSpan.FromMinutes(5);

    private readonly IComposeCommandRunner ttsRunner;
    private readonly ISttComposeRunner sttRunner;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SpeechSidecarFaultRecovery> logger;

    // Pre-populated in the ctor and never mutated afterwards, so concurrent reads
    // need no lock; the per-sidecar counters themselves are updated with Interlocked.
    private readonly Dictionary<SpeechSidecar, SidecarState> states = new()
    {
        [SpeechSidecar.Tts] = new SidecarState(),
        [SpeechSidecar.Stt] = new SidecarState(),
    };

    public SpeechSidecarFaultRecovery(
        IComposeCommandRunner ttsRunner,
        ISttComposeRunner sttRunner,
        TimeProvider timeProvider,
        ILogger<SpeechSidecarFaultRecovery> logger)
    {
        this.ttsRunner = ttsRunner;
        this.sttRunner = sttRunner;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>The most recently kicked-off recovery task, if any. Test seam.</summary>
    internal Task? PendingRecovery { get; private set; }

    /// <summary>Current fault streak for a sidecar. Test seam.</summary>
    internal int ConsecutiveFaults(SpeechSidecar sidecar)
        => Volatile.Read(ref this.states[sidecar].ConsecutiveFaults);

    /// <inheritdoc />
    public void OnSidecarSuccess(SpeechSidecar sidecar)
        => Interlocked.Exchange(ref this.states[sidecar].ConsecutiveFaults, 0);

    /// <inheritdoc />
    public void OnSidecarFault(SpeechSidecar sidecar, string detail, int statusCode)
    {
        // Only server-side faults indicate a sick sidecar. A 4xx means we asked for
        // something it can't serve — restarting would hide our own bug.
        if (statusCode is < 500 or >= 600)
        {
            return;
        }

        if (Interlocked.Increment(ref this.states[sidecar].ConsecutiveFaults) < FaultThreshold)
        {
            return;
        }

        // Fire-and-forget: this runs on the speech hot path. TryRecoverAsync never
        // throws, so the task can't fault unobserved.
        this.PendingRecovery = Task.Run(() => this.TryRecoverAsync(sidecar, CancellationToken.None));
    }

    /// <summary>
    /// Restarts the sidecar when its fault threshold is met and its cooldown has elapsed.
    /// Returns true only when a restart actually succeeded. Never throws.
    /// </summary>
    internal async Task<bool> TryRecoverAsync(SpeechSidecar sidecar, CancellationToken cancellationToken)
    {
        var state = this.states[sidecar];
        if (Interlocked.CompareExchange(ref state.Recovering, 1, 0) != 0)
        {
            return false; // a restart of this sidecar is already in flight
        }

        try
        {
            var faults = Volatile.Read(ref state.ConsecutiveFaults);
            if (faults < FaultThreshold)
            {
                return false;
            }

            var now = this.timeProvider.GetUtcNow();
            if (state.LastRestart is { } previous && now - previous < RestartCooldown)
            {
                this.LogRestartSuppressed(sidecar, (int)(now - previous).TotalSeconds);
                return false;
            }

            this.LogRestarting(sidecar, faults);
            state.LastRestart = now;

            var restarted = sidecar switch
            {
                SpeechSidecar.Tts => await this.ttsRunner.RestartDanishAsync(cancellationToken).ConfigureAwait(false),
                SpeechSidecar.Stt => await this.sttRunner.RestartSttAsync(cancellationToken).ConfigureAwait(false),
                _ => false,
            };

            // Reset either way: on success the streak is stale, and on failure the
            // cooldown — not the counter — governs the next attempt.
            Interlocked.Exchange(ref state.ConsecutiveFaults, 0);

            if (restarted)
            {
                this.LogRestarted(sidecar);
            }
            else
            {
                this.LogRestartFailed(sidecar);
            }

            return restarted;
        }
        catch (Exception ex)
        {
            this.LogRecoveryError(sidecar, ex.Message);
            return false;
        }
        finally
        {
            Volatile.Write(ref state.Recovering, 0);
        }
    }

    private sealed class SidecarState
    {
        public int ConsecutiveFaults;
        public int Recovering; // 0 = idle, 1 = a restart is in flight
        public DateTimeOffset? LastRestart;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Sidecar} sidecar failed {Faults} consecutive requests while reporting healthy — restarting it to clear a poisoned GPU context")]
    private partial void LogRestarting(SpeechSidecar sidecar, int faults);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sidecar} sidecar restarted — speech should recover")]
    private partial void LogRestarted(SpeechSidecar sidecar);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Sidecar} sidecar restart failed — voice stays degraded")]
    private partial void LogRestartFailed(SpeechSidecar sidecar);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Sidecar} sidecar still failing but last restart was {ElapsedSeconds}s ago — suppressed by cooldown")]
    private partial void LogRestartSuppressed(SpeechSidecar sidecar, int elapsedSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Sidecar} sidecar fault recovery error: {Error}")]
    private partial void LogRecoveryError(SpeechSidecar sidecar, string error);
}
