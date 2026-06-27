using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Ensures the STT sidecar (whisper-stt, container <c>cortex-stt</c>) is running
/// while the Bridge is up. STT is needed by every voice path (desktop voice,
/// Discord voice), so it is started unconditionally — start-if-down; the sidecar
/// is never stopped here. Mirrors <see cref="DanishTtsLifecycle"/>. Reconcile is
/// self-contained: failures are logged, never propagated, so fire-and-forget
/// startup can't produce an unobserved task exception.
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

    /// <summary>Starts the cortex-stt sidecar if it isn't already running.</summary>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsSttRunningAsync(cancellationToken).ConfigureAwait(false);
            if (!running)
            {
                this.LogStarting();
                await this.runner.StartSttAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "whisper-stt sidecar down — starting cortex-stt")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Warning, Message = "whisper-stt reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
