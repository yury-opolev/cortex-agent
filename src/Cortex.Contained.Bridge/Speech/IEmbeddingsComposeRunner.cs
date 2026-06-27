namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker-compose CLI for the cortex-embeddings sidecar.</summary>
public interface IEmbeddingsComposeRunner
{
    /// <summary>`docker compose --profile memory up -d embeddings`. Returns true on exit 0.</summary>
    Task<bool> StartEmbeddingsAsync(CancellationToken cancellationToken);

    /// <summary>`docker compose --profile memory stop embeddings`. Returns true on exit 0.</summary>
    Task<bool> StopEmbeddingsAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-embeddings` container is currently running.</summary>
    Task<bool> IsEmbeddingsRunningAsync(CancellationToken cancellationToken);
}
