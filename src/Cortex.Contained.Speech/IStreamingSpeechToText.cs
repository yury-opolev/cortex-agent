namespace Cortex.Contained.Speech;

/// <summary>
/// Streaming speech-to-text engine. Accepts audio frame-by-frame and produces
/// partial transcription results in real-time, used for low-latency voice
/// interaction (agent starts thinking before the user finishes speaking).
/// </summary>
/// <remarks>
/// Unlike <see cref="ISpeechToText"/> (batch), this interface processes audio
/// incrementally and surfaces partial results as more audio arrives. Implementations
/// are expected to run any actual inference on a background task so that <see
/// cref="AcceptAudio"/> stays cheap (callers feed audio from a hot receive loop).
/// A <em>committed</em> prefix of the transcript stabilizes over time using an
/// agreement policy (e.g. LocalAgreement-2); the trailing <em>provisional</em>
/// suffix can still change.
/// </remarks>
public interface IStreamingSpeechToText : IDisposable
{
    /// <summary>
    /// Feed a chunk of 16kHz mono 16-bit PCM audio to the recognizer.
    /// Call repeatedly as audio frames arrive. Must not block on inference.
    /// </summary>
    /// <param name="pcm16kMono">Raw PCM audio data (16kHz, mono, 16-bit signed).</param>
    void AcceptAudio(ReadOnlySpan<byte> pcm16kMono);

    /// <summary>
    /// Get the current best-effort transcription: committed tokens plus the
    /// latest unstable suffix. May change as more audio arrives. Returns empty
    /// string if no speech has been recognized yet.
    /// </summary>
    string GetPartialResult();

    /// <summary>
    /// Flush any pending audio, run a final transcription pass, and return
    /// the complete transcription for the current utterance. The caller should
    /// typically call <see cref="Reset"/> afterwards to start the next utterance.
    /// </summary>
    Task<string> GetFinalResultAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discard all accumulated audio and reset the recognizer state.
    /// Call this to start fresh for a new utterance.
    /// </summary>
    void Reset();

    /// <summary>Whether the streaming STT engine is initialized and ready.</summary>
    bool IsReady { get; }
}
