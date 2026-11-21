using MemoryMcp.Core.Services;

namespace Cortex.Contained.Agent.Host.Tests.Memory.Evaluation;

/// <summary>
/// Deterministic embedder for memory tests: a hashed bag of folded words, with a tiny synonym map so a
/// paraphrase ("railway journey") lands near its source ("train trip"). Never calls Ollama.
/// </summary>
internal sealed class FakeMemoryEmbedder : IEmbeddingService
{
    public const int Dimensions = 32;

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["railway"] = "train",
        ["journey"] = "trip",
        ["voyage"] = "trip",
    };

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToList());

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var token in KeywordTokenizer.Tokenize(TextFolder.Fold(text)))
        {
            var word = Synonyms.GetValueOrDefault(token, token);
            var bucket = (int)(word.Aggregate(17u, (hash, c) => unchecked((hash * 31) + c)) % Dimensions);
            vector[bucket] += 1;
        }

        vector[Dimensions - 1] += 0.01f;
        return VectorMath.Normalize(vector);
    }
}
