using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>
/// Converges the cortex-embeddings sidecar with the built-in-memory enable flag:
/// start-if-down when enabled, stop-if-up when disabled. Failures are logged, never propagated.
/// </summary>
public sealed partial class EmbeddingsSidecarLifecycle
{
    private readonly IEmbeddingsComposeRunner runner;
    private readonly ILogger<EmbeddingsSidecarLifecycle> logger;

    public EmbeddingsSidecarLifecycle(IEmbeddingsComposeRunner runner, ILogger<EmbeddingsSidecarLifecycle> logger)
    {
        this.runner = runner;
        this.logger = logger;
    }

    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var running = await this.runner.IsEmbeddingsRunningAsync(cancellationToken).ConfigureAwait(false);
            if (enabled && !running)
            {
                this.LogStarting();
                await this.runner.StartEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!enabled && running)
            {
                this.LogStopping();
                await this.runner.StopEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            this.LogReconcileFailed(ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "memory enabled — starting cortex-embeddings")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "memory disabled — stopping cortex-embeddings")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-embeddings reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
