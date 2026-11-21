using System.Text;
using Cortex.Contained.Agent.Host.History;
using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>
/// Deterministic test embedder (no Ollama): a hashed bag of folded words, with optional synonyms
/// mapped to one concept so paraphrases land close together.
/// </summary>
internal sealed class FakeHistoryEmbedder : IHistoryEmbedder
{
    public const int Dimensions = 64;

    private readonly IReadOnlyDictionary<string, string> synonyms;

    public FakeHistoryEmbedder(IReadOnlyDictionary<string, string>? synonyms = null)
    {
        this.synonyms = synonyms ?? new Dictionary<string, string>();
    }

    public string Fingerprint { get; set; } = "fake-embedder-v1";

    public string? UnavailableReason { get; set; }

    /// <summary>Texts containing this marker are rejected as too long for the context window.</summary>
    public string? RejectMarker { get; set; }

    /// <summary>When set, every call throws it (embedder down).</summary>
    public Exception? Failure { get; set; }

    public List<string> Documents { get; } = [];

    public List<string> Queries { get; } = [];

    public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (this.Failure is { } failure)
        {
            throw failure;
        }

        if (this.RejectMarker is { } marker && texts.Any(t => t.Contains(marker, StringComparison.Ordinal)))
        {
            throw new EmbeddingInputTooLongException("too long");
        }

        this.Documents.AddRange(texts);
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(this.Vector).ToList());
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        if (this.Failure is { } failure)
        {
            throw failure;
        }

        this.Queries.Add(query);
        return Task.FromResult(this.Vector(query));
    }

    public float[] Vector(string text)
    {
        var vector = new float[Dimensions];
        foreach (var token in HistoryTokenizer.Tokenize(TextFolder.Fold(text)))
        {
            var concept = this.synonyms.TryGetValue(token, out var mapped) ? mapped : token;
            vector[Bucket(concept)] += 1;
        }

        return VectorMath.Normalize(vector);
    }

    private static int Bucket(string token)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash = unchecked((hash ^ b) * 16777619u);
        }

        return (int)(hash % Dimensions);
    }
}
