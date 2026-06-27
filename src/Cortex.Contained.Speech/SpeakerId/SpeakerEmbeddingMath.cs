namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Static helpers for working with speaker-embedding vectors. All operations
/// are pure: inputs are never mutated.
/// </summary>
public static class SpeakerEmbeddingMath
{
    /// <summary>
    /// Returns a copy of <paramref name="vector"/> rescaled to unit L2 length.
    /// A zero vector is returned as a fresh zero-vector of the same length.
    /// </summary>
    public static float[] L2Normalise(ReadOnlySpan<float> vector)
    {
        float sumSquares = 0.0f;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        var result = new float[vector.Length];
        if (sumSquares <= 0.0f)
        {
            return result;
        }

        var inverseNorm = 1.0f / MathF.Sqrt(sumSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            result[i] = vector[i] * inverseNorm;
        }

        return result;
    }

    /// <summary>
    /// Cosine similarity between two equal-length vectors. The vectors do NOT
    /// need to be pre-normalised. Returns a value in [-1, 1]; returns 0 if
    /// either vector has zero magnitude.
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Embedding vectors must have the same length.");
        }

        float dot = 0.0f;
        float aSquared = 0.0f;
        float bSquared = 0.0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aSquared += a[i] * a[i];
            bSquared += b[i] * b[i];
        }

        if (aSquared <= 0.0f || bSquared <= 0.0f)
        {
            return 0.0f;
        }

        return dot / (MathF.Sqrt(aSquared) * MathF.Sqrt(bSquared));
    }

    /// <summary>
    /// Computes the L2-normalised mean of L2-normalised inputs. Used to
    /// aggregate multiple enrollment samples into a single voiceprint.
    /// </summary>
    public static float[] MeanOfNormalised(IReadOnlyList<float[]> embeddings)
    {
        if (embeddings.Count == 0)
        {
            throw new ArgumentException("At least one embedding is required.", nameof(embeddings));
        }

        var dim = embeddings[0].Length;
        var accumulator = new float[dim];

        foreach (var embedding in embeddings)
        {
            if (embedding.Length != dim)
            {
                throw new ArgumentException("All embeddings must have the same length.", nameof(embeddings));
            }

            var normalised = L2Normalise(embedding);
            for (var i = 0; i < dim; i++)
            {
                accumulator[i] += normalised[i];
            }
        }

        for (var i = 0; i < dim; i++)
        {
            accumulator[i] /= embeddings.Count;
        }

        return L2Normalise(accumulator);
    }
}
