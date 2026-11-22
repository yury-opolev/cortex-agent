namespace Cortex.Contained.Agent.Host.Reranking;

/// <summary>The reranker's relevance score for one document of a request.</summary>
/// <param name="Index">Zero-based index of the document in the request.</param>
/// <param name="Score">Relevance; higher is better.</param>
public sealed record RerankScore(int Index, double Score);

/// <summary>A cross-encoder reranker (the <c>cortex-reranker</c> sidecar in production).</summary>
public interface IReranker
{
    /// <summary>Why no text may be sent right now (exposure guard, missing key), or null when it may.</summary>
    string? UnavailableReason { get; }

    /// <summary>Scores every document against <paramref name="query"/>; throws <see cref="RerankerException"/> on failure.</summary>
    Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken);
}

/// <summary>A reranker call failed. Messages never contain query or document text.</summary>
public sealed class RerankerException : Exception
{
    public RerankerException()
    {
    }

    public RerankerException(string message)
        : base(message)
    {
    }

    public RerankerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
