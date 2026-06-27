using Cortex.Contained.Contracts.Config;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Ensures the unified TTS sidecar (uni-voices) is running while the Bridge is
/// up. uni-voices is the TTS engine for every language now (Kokoro, Røst,
/// Silero behind one API), so it is started unconditionally — independent of
/// whether any Danish/roest-da voice is configured. Called at Bridge startup
/// and after settings saves. Start-if-down; the sidecar is never stopped here.
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
    /// Ensures the uni-voices sidecar is running. The <paramref name="tts"/>
    /// config is accepted for call-site symmetry but no longer gates start-up:
    /// the sidecar serves all TTS, so it always runs while the Bridge is up.
    /// </summary>
    public async Task ReconcileAsync(TtsConfig tts, CancellationToken cancellationToken)
    {
        _ = tts;

        // Robust to any IComposeCommandRunner impl: a thrown reconcile is logged,
        // not propagated. Protects fire-and-forget startup (no unobserved task
        // exception) and the save path (no HTTP 500 if the runner throws).
        try
        {
            var running = await this.runner.IsDanishRunningAsync(cancellationToken).ConfigureAwait(false);

            if (!running)
            {
                this.LogStarting();
                await this.runner.StartDanishAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "uni-voices TTS sidecar down — starting uni-voices")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Warning, Message = "uni-voices TTS reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
