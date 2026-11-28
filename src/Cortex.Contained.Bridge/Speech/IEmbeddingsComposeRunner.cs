namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker CLI for the cortex-embeddings sidecar.</summary>
public interface IEmbeddingsComposeRunner
{
    /// <summary>
    /// `docker compose [-f override] --profile memory up -d [--force-recreate] --wait embeddings`: the NVIDIA
    /// override file is added only when <paramref name="useGpu"/> is true, the loopback-port override only
    /// when <paramref name="publishToHost"/> is true, <paramref name="recreate"/> forces a new container
    /// (a changed placement or exposure). Waits for the container's healthcheck. Returns true on exit 0.
    /// </summary>
    Task<bool> StartEmbeddingsAsync(bool useGpu, bool recreate, bool publishToHost, CancellationToken cancellationToken);

    /// <summary>`docker compose --profile memory stop embeddings`. Returns true on exit 0.</summary>
    Task<bool> StopEmbeddingsAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-embeddings` container is currently running.</summary>
    Task<bool> IsEmbeddingsRunningAsync(CancellationToken cancellationToken);

    /// <summary>True if the running container was created with a GPU device request (`docker inspect`).</summary>
    Task<bool> IsEmbeddingsOnGpuAsync(CancellationToken cancellationToken);

    /// <summary>True if the running container was created with a published host port (`docker inspect`).</summary>
    Task<bool> IsEmbeddingsPublishedToHostAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The PROCESSOR column of `ollama ps` inside the container (for example <c>100% GPU</c>), or null when
    /// no model is loaded at the moment or the container cannot be queried.
    /// </summary>
    Task<string?> GetEmbeddingsProcessorAsync(CancellationToken cancellationToken);
}
