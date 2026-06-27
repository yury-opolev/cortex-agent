namespace Cortex.Contained.Speech;

/// <summary>
/// A transcription result that includes per-token timestamps in addition to
/// the joined text. Used by streaming consumers that need to map text back
/// onto audio offsets — e.g. the trim-by-committed-timestamp logic in
/// <see cref="Stt.WhisperStreamingSpeechToText"/>.
/// </summary>
/// <param name="Text">The full transcribed text, joined across segments.</param>
/// <param name="Tokens">
/// Per-token timestamps. Empty when the underlying <see cref="ISpeechToText"/>
/// implementation doesn't expose token-level information (the default
/// interface implementation falls back to text-only).
/// </param>
public sealed record DetailedTranscription(string Text, IReadOnlyList<TranscribedToken> Tokens)
{
    /// <summary>Convenience constructor for text-only results (no token timing).</summary>
    public DetailedTranscription(string text) : this(text, []) { }
}
