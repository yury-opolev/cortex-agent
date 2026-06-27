using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the unified TTS sidecar (uni-voices) with the desired run-state:
/// when enabled, start-if-down; when disabled, stop-if-up. Called at Bridge
/// startup and after settings saves. Reconcile is self-contained: failures are
/// logged, not propagated, so fire-and-forget callers can't produce an
/// unobserved task exception.
/// </summary>
public sealed partial class DanishTtsLifecycle
{
    private readonly IComposeCommandRunner runner;
    private readonly ILogger<DanishTtsLifecycle> logger;

    public DanishTtsLifecycle(IComposeCommandRunner runner, ILogger<DanishTtsLifecycle> logger)
    {
        this.runner = runner;
        this.logger = logger;
    }

    /// <summary>
    /// Converges the uni-voices sidecar with the desired run-state: start-if-down when
    /// <paramref name="enabled"/>, stop-if-up otherwise. Failures are logged, not propagated.
    /// </summary>
    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsDanishRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                await this.runner.StartDanishAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!enabled && running)
            {
                this.LogStopping();
                await this.runner.StopDanishAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "uni-voices TTS sidecar down — starting uni-voices")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "uni-voices TTS disabled — stopping uni-voices")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "uni-voices TTS reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
