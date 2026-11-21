using Cortex.Contained.Agent.Host.Reranking;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary>Deterministic test reranker (no sidecar): scores each document with <see cref="Score"/>.</summary>
internal sealed class FakeReranker : IReranker
{
    public string? UnavailableReason { get; set; }

    /// <summary>Relevance of a document for a query; by default the number of query words it contains.</summary>
    public Func<string, string, double> Score { get; set; } = (query, document) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(word => document.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>When set, every call throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, every call waits this long first (cancellable).</summary>
    public TimeSpan? Delay { get; set; }

    public List<(string Query, IReadOnlyList<string> Documents)> Calls { get; } = [];

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        this.Calls.Add((query, documents));
        if (this.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (this.Failure is { } failure)
        {
            throw failure;
        }

        return documents.Select((document, index) => new RerankScore(index, this.Score(query, document))).ToList();
    }
}
