using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Bridge.Speech;

/// <summary>What the Bridge knows about the cortex-embeddings sidecar after its last reconcile.</summary>
/// <param name="State"><c>unknown</c> (never reconciled), <c>starting</c>, <c>running</c>, <c>stopped</c> or <c>failed</c>.</param>
/// <param name="Problem">What went wrong (or that the GPU was not used) in words the owner can act on; null otherwise.</param>
/// <param name="At">When the state was recorded, or null.</param>
public sealed record EmbeddingsSidecarStatus(string State, string? Problem, DateTimeOffset? At)
{
    /// <summary>Never reconciled.</summary>
    public static EmbeddingsSidecarStatus Unknown { get; } = new("unknown", null, null);
}

/// <summary>
/// Converges the cortex-embeddings sidecar with the built-in-memory enable flag, the GPU switch and the
/// host-port switch: start-if-down when enabled, stop-if-up when disabled, and a recreate when the running
/// container's placement (CPU or GPU) or exposure (internal only, or also published on the host's loopback
/// interface) differs from the switches. When the GPU start fails (no NVIDIA runtime) it falls
/// back to the CPU so memory keeps working, and says so in <see cref="Status"/>. Failures are logged and
/// reported, never propagated.
/// </summary>
public sealed partial class EmbeddingsSidecarLifecycle : IDisposable
{
    /// <summary>Shown when the GPU start failed and the sidecar runs on the CPU instead.</summary>
    public const string GpuFallbackProblem =
        "The embeddings sidecar could not start on the GPU (it needs an NVIDIA GPU with Docker GPU support), " +
        "so it is running on the CPU instead. Details are in the Bridge log.";

    /// <summary>Shown when compose could not start the container at all.</summary>
    public const string StartFailedProblem =
        "docker compose could not start cortex-embeddings. Embedding-based recall is unavailable until it " +
        "runs. Details are in the Bridge log.";

    private readonly IEmbeddingsComposeRunner runner;
    private readonly Func<bool> useGpu;
    private readonly Func<bool> publishToHost;
    private readonly ILogger<EmbeddingsSidecarLifecycle> logger;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile EmbeddingsSidecarStatus status = EmbeddingsSidecarStatus.Unknown;

    public EmbeddingsSidecarLifecycle(
        IEmbeddingsComposeRunner runner,
        Func<bool> useGpu,
        ILogger<EmbeddingsSidecarLifecycle> logger,
        TimeProvider? time = null,
        Func<bool>? publishToHost = null)
    {
        this.runner = runner;
        this.useGpu = useGpu;
        this.publishToHost = publishToHost ?? (() => false);
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>The state recorded by the last reconcile.</summary>
    public EmbeddingsSidecarStatus Status => this.status;

    public async Task ReconcileAsync(bool enabled, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.ReconcileCoreAsync(enabled, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A sidecar problem must never take the Bridge down
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogReconcileFailed(ex.Message);
            this.Record("failed", "The embeddings sidecar could not be reconciled: " + ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            this.gate.Release();
        }
    }

    private async Task ReconcileCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        var running = await this.runner.IsEmbeddingsRunningAsync(cancellationToken).ConfigureAwait(false);
        if (!enabled)
        {
            if (running)
            {
                this.LogStopping();
                await this.runner.StopEmbeddingsAsync(cancellationToken).ConfigureAwait(false);
            }

            this.Record("stopped", null);
            return;
        }

        var wantGpu = this.useGpu();
        var wantHostPort = this.publishToHost();
        if (running
            && await this.runner.IsEmbeddingsOnGpuAsync(cancellationToken).ConfigureAwait(false) == wantGpu
            && await this.runner.IsEmbeddingsPublishedToHostAsync(cancellationToken).ConfigureAwait(false) == wantHostPort)
        {
            this.Record("running", null);
            return;
        }

        this.Record("starting", null);
        this.LogStarting(wantGpu ? "GPU" : "CPU", wantHostPort ? "published on 127.0.0.1" : "internal only");
        if (await this.StartAsync(wantGpu, recreate: running, wantHostPort, cancellationToken).ConfigureAwait(false))
        {
            this.Record("running", null);
            return;
        }

        if (wantGpu)
        {
            this.LogGpuStartFailed();
            if (await this.StartAsync(useGpu: false, recreate: true, wantHostPort, cancellationToken).ConfigureAwait(false))
            {
                this.Record("running", GpuFallbackProblem);
                return;
            }
        }

        this.LogStartFailed();
        this.Record("failed", StartFailedProblem);
    }

    private async Task<bool> StartAsync(bool useGpu, bool recreate, bool publishToHost, CancellationToken cancellationToken) =>
        await this.runner.StartEmbeddingsAsync(useGpu, recreate, publishToHost, cancellationToken).ConfigureAwait(false)
        && await this.runner.IsEmbeddingsRunningAsync(cancellationToken).ConfigureAwait(false);

    private void Record(string state, string? problem) =>
        this.status = new EmbeddingsSidecarStatus(state, problem, this.time.GetUtcNow());

    /// <inheritdoc />
    public void Dispose() => this.gate.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "memory enabled — converging cortex-embeddings on the {Placement} ({Exposure})")]
    private partial void LogStarting(string placement, string exposure);

    [LoggerMessage(Level = LogLevel.Information, Message = "memory disabled — stopping cortex-embeddings")]
    private partial void LogStopping();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-embeddings did not start on the GPU; falling back to the CPU")]
    private partial void LogGpuStartFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-embeddings did not start; memory search keeps working without dense matches until it does")]
    private partial void LogStartFailed();

    [LoggerMessage(Level = LogLevel.Warning, Message = "cortex-embeddings reconcile failed: {Error}")]
    private partial void LogReconcileFailed(string error);
}
