namespace Cortex.Contained.Bridge.Speech;

/// <summary>Narrow seam over the docker-compose CLI for the cortex-reranker sidecar.</summary>
public interface IRerankerComposeRunner
{
    /// <summary>
    /// `docker compose --profile reranker up -d reranker` with <paramref name="apiKey"/> in the process
    /// environment (<c>CORTEX_RERANKER_API_KEY</c>, never on the command line). Compose recreates the
    /// container when the key or the <c>cortex-reranker:latest</c> image changed. Returns true on exit 0.
    /// </summary>
    Task<bool> StartRerankerAsync(string apiKey, CancellationToken cancellationToken);

    /// <summary>`docker compose --profile reranker stop reranker`. Returns true on exit 0.</summary>
    Task<bool> StopRerankerAsync(CancellationToken cancellationToken);

    /// <summary>True if the `cortex-reranker` container is currently running.</summary>
    Task<bool> IsRerankerRunningAsync(CancellationToken cancellationToken);

    /// <summary>True if the <c>cortex-reranker:latest</c> image exists locally (compose never pulls it).</summary>
    Task<bool> HasRerankerImageAsync(CancellationToken cancellationToken);
}
