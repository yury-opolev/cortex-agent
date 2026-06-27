namespace Cortex.Contained.Speech;

/// <summary>
/// A single token emitted by a speech-to-text engine, with its time bounds in
/// the input audio. Used by streaming consumers (e.g. the trim-by-committed-
/// timestamp logic in <see cref="Stt.WhisperStreamingSpeechToText"/>) to map
/// transcribed text back to audio offsets.
/// </summary>
/// <param name="Text">The token's text. May include leading whitespace.</param>
/// <param name="StartMs">Start time of the token in the passed audio, in milliseconds.</param>
/// <param name="EndMs">End time of the token in the passed audio, in milliseconds.</param>
public sealed record TranscribedToken(string Text, int StartMs, int EndMs);
