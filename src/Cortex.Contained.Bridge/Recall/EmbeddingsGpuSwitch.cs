using Cortex.Contained.Bridge.Speech;
using Cortex.Contained.Contracts.Config;

namespace Cortex.Contained.Bridge.Recall;

/// <summary>The Bridge-wide embeddings GPU switch as the Recall page shows it.</summary>
/// <param name="Enabled"><c>Embeddings:Gpu</c> in the Bridge configuration (shared by every tenant).</param>
/// <param name="Sidecar">The sidecar state after the last reconcile.</param>
/// <param name="Mode">Where the running container was created to run: <c>gpu</c> or <c>cpu</c>; null when it is not running or not probed.</param>
/// <param name="Processor">What Ollama reports for the loaded model (<c>100% GPU</c>); null when no model is loaded or not probed.</param>
public sealed record EmbeddingsGpuView(bool Enabled, EmbeddingsSidecarStatus Sidecar, string? Mode, string? Processor);

/// <summary>
/// Owns <c>Embeddings:Gpu</c>: persists the switch to <c>cortex.yml</c>, then converges the sidecar — the
/// lifecycle recreates the container when its placement differs and falls back to the CPU when the GPU start
/// fails.
/// </summary>
public sealed class EmbeddingsGpuSwitch
{
    private readonly BridgeConfig config;
    private readonly Action<BridgeConfig> persist;
    private readonly EmbeddingsSidecarLifecycle lifecycle;
    private readonly IEmbeddingsComposeRunner runner;

    public EmbeddingsGpuSwitch(
        BridgeConfig config,
        Action<BridgeConfig> persist,
        EmbeddingsSidecarLifecycle lifecycle,
        IEmbeddingsComposeRunner runner)
    {
        this.config = config;
        this.persist = persist;
        this.lifecycle = lifecycle;
        this.runner = runner;
    }

    /// <summary>The switch and the sidecar state after the last reconcile (no docker calls).</summary>
    public EmbeddingsGpuView Current => new(this.config.Embeddings.Gpu, this.lifecycle.Status, null, null);

    /// <summary>Persists <paramref name="enabled"/> (synchronously, so a failure surfaces to the caller); the previous value is restored when persisting fails.</summary>
    public void Set(bool enabled)
    {
        var previous = this.config.Embeddings.Gpu;
        this.config.Embeddings.Gpu = enabled;
        try
        {
            this.persist(this.config);
        }
        catch
        {
            this.config.Embeddings.Gpu = previous;
            throw;
        }
    }

    /// <summary>Converges the sidecar with the persisted switch and the memory flag. Never throws.</summary>
    public Task ConvergeAsync(CancellationToken cancellationToken) =>
        this.lifecycle.ReconcileAsync(this.config.Memory.Enabled, cancellationToken);

    /// <summary>
    /// <see cref="Current"/> plus where the container really runs: the GPU device request of the running
    /// container (`docker inspect`) and the PROCESSOR Ollama reports for the loaded model. Never throws.
    /// </summary>
    public async Task<EmbeddingsGpuView> ProbeAsync(CancellationToken cancellationToken)
    {
        var view = this.Current;
        try
        {
            if (!await this.runner.IsEmbeddingsRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                return view;
            }

            var onGpu = await this.runner.IsEmbeddingsOnGpuAsync(cancellationToken).ConfigureAwait(false);
            var processor = await this.runner.GetEmbeddingsProcessorAsync(cancellationToken).ConfigureAwait(false);
            return view with { Mode = onGpu ? "gpu" : "cpu", Processor = processor };
        }
#pragma warning disable CA1031 // A status probe must never fail the Recall page
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return view;
        }
#pragma warning restore CA1031
    }
}
