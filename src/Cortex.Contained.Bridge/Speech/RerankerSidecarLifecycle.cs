using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the cortex-reranker sidecar with <c>Reranker:Enabled</c>: when enabled it always runs
/// `compose up` with the DPAPI-held API key (idempotent; compose recreates the container after a key
/// or image change, which is how a Self-Update image upgrade or rollback reaches the sidecar); when
/// disabled it stops a running sidecar. Failures are logged, never propagated.
/// </summary>
public sealed partial class RerankerSidecarLifecycle
{
    private readonly IRerankerComposeRunner runner;
    private readonly Func<string> apiKey;
    private readonly ILogger<RerankerSidecarLifecycle> logger;

    public RerankerSidecarLifecycle(IRerankerComposeRunner runner, Func<string> apiKey, ILogger<RerankerSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.apiKey = apiKey;
        this.logger = logger;
    }

    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (enabled)
            {
                this.LogConverging();
                if (!await this.runner.StartRerankerAsync(this.apiKey(), cancellationToken).ConfigureAwait(false))
                {
                    this.LogStartFailed();
                }
            }
            else if (await this.runner.IsRerankerRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                this.LogStopping();
                await this.runner.StopRerankerAsync(cancellationToken).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A sidecar problem must never take the Bridge down
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "reranker enabled — converging cortex-reranker (compose up)")]
    private partial void LogConverging();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-reranker did not start; searches keep the fused order until it does")]
    private partial void LogStartFailed();

    [LoggerMessage(Level = LogLevel.Information, Message = "reranker disabled — stopping cortex-reranker")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-reranker reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
