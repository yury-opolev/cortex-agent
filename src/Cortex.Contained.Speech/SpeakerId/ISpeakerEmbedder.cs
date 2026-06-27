namespace Cortex.Contained.Speech.SpeakerId;

/// <summary>
/// Embeds a single utterance into a fixed-length L2-normalised speaker vector.
/// </summary>
/// <remarks>
/// Accepts raw 16-bit mono 16 kHz PCM. Implementations are responsible for
/// feature extraction (e.g. fbank) internally so the same interface can serve
/// both the local ONNX embedder and a remote HTTP embedder.
/// </remarks>
public interface ISpeakerEmbedder
{
    /// <summary>
    /// Identifier of the underlying model variant. Used to detect model
    /// swaps when verifying against a stored voiceprint.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Output dimensionality (e.g. 192 for ERes2NetV2). Constant across
    /// inferences for a given embedder instance.
    /// </summary>
    int EmbeddingDim { get; }

    /// <summary>
    /// Runs feature extraction and inference, returning the L2-normalised
    /// embedding. Returns an empty array when the input is too short to
    /// produce any fbank frames.
    /// </summary>
    /// <param name="pcm16Mono16k">Raw 16-bit mono 16 kHz PCM samples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<float[]> EmbedAsync(ReadOnlyMemory<short> pcm16Mono16k, CancellationToken cancellationToken);
}
