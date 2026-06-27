using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the cortex-voice-id sidecar with the effective voice-id enable flag:
/// start-if-down when enabled, stop-if-up when disabled. <see cref="ReconcileAsync"/> is the
/// fire-and-forget startup path; <see cref="TryReconcileNowAsync"/> performs the same work but
/// returns whether the live compose op was confirmed (false ⇒ UI shows "restart required").
/// </summary>
public sealed partial class VoiceIdSidecarLifecycle
{
    private readonly IVoiceIdComposeRunner runner;
    private readonly ILogger<VoiceIdSidecarLifecycle> logger;

    public VoiceIdSidecarLifecycle(IVoiceIdComposeRunner runner, ILogger<VoiceIdSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.logger = logger;
    }

    public Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
        => this.TryReconcileNowAsync(enabled, cancellationToken);

    /// <summary>Reconcile and report whether the desired state was achieved (true) or a live
    /// compose op failed/threw (false). Never throws.</summary>
    public async Task<bool> TryReconcileNowAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsVoiceIdRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                return await this.runner.StartVoiceIdAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!enabled && running)
            {
                this.LogStopping();
                return await this.runner.StopVoiceIdAsync(cancellationToken).ConfigureAwait(false);
            }

            return true; // already in desired state
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-id enabled — starting cortex-voice-id")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-id disabled — stopping cortex-voice-id")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-voice-id reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
