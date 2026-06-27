using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the STT sidecar (whisper-stt, container <c>cortex-stt</c>) with the
/// desired run-state: when enabled, start-if-down; when disabled, stop-if-up.
/// Mirrors <see cref="DanishTtsLifecycle"/>. Reconcile is self-contained: failures
/// are logged, never propagated, so fire-and-forget startup can't produce an
/// unobserved task exception.
/// </summary>
public sealed partial class SttSidecarLifecycle
{
    private readonly ISttComposeRunner runner;
    private readonly ILogger<SttSidecarLifecycle> logger;

    public SttSidecarLifecycle(ISttComposeRunner runner, ILogger<SttSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.logger = logger;
    }

    /// <summary>
    /// Converges the cortex-stt sidecar with the desired run-state: when
    /// <paramref name="enabled"/> is true, start it if down; when false, stop it
    /// if up. Failures are logged, never propagated.
    /// </summary>
    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsSttRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                await this.runner.StartSttAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!enabled && running)
            {
                this.LogStopping();
                await this.runner.StopSttAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "whisper-stt sidecar down — starting cortex-stt")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "whisper-stt disabled — stopping cortex-stt")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "whisper-stt reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
