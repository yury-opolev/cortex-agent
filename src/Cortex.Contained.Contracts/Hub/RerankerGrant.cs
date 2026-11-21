namespace Cortex.Contained.Contracts.Hub;

/// <summary>
/// The reranker switch and the <c>cortex-reranker</c> sidecar's API key, pushed by the Bridge on every
/// connect (the key is generated once and kept in DPAPI storage). The agent holds it in memory only.
/// </summary>
public sealed record RerankerGrant
{
    /// <summary>Reranking is enabled in the Bridge configuration (<c>Reranker:Enabled</c>).</summary>
    public bool Enabled { get; init; }

    /// <summary>The sidecar's API key; null when reranking is disabled.</summary>
    public string? ApiKey { get; init; }
}
