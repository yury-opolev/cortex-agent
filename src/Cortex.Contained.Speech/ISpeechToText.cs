namespace Cortex.Contained.Speech;

/// <summary>
/// Speech-to-text engine. Converts audio PCM data to text.
/// </summary>
public interface ISpeechToText : IDisposable
{
    /// <summary>
    /// Transcribe audio data to text.
    /// </summary>
    /// <param name="pcmData">16kHz mono 16-bit PCM audio samples.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transcribed text, or null if no speech was detected.</returns>
    Task<string?> TranscribeAsync(byte[] pcmData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transcribe audio data with optional initial prompt and return per-token
    /// timestamps alongside the joined text. Used by streaming consumers that
    /// need to map text back onto audio offsets (e.g. trim-by-committed-token
    /// in <see cref="Stt.WhisperStreamingSpeechToText"/>).
    /// <para>
    /// The default implementation falls back to <see cref="TranscribeAsync"/>
    /// and returns a result with empty <see cref="DetailedTranscription.Tokens"/>.
    /// Engines that natively support token timestamps (Whisper) override this
    /// to populate the token list.
    /// </para>
    /// </summary>
    /// <param name="pcmData">16kHz mono 16-bit PCM audio samples.</param>
    /// <param name="prompt">
    /// Optional initial prompt that biases decoding for continuity (e.g. the
    /// text already committed in earlier streaming passes). Engines that don't
    /// support prompts ignore this argument silently.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Detailed transcription with text and (optionally) tokens, or null if
    /// no speech was detected.
    /// </returns>
    async Task<DetailedTranscription?> TranscribeDetailedAsync(
        byte[] pcmData, string? prompt, CancellationToken cancellationToken = default)
    {
        var text = await TranscribeAsync(pcmData, cancellationToken).ConfigureAwait(false);
        return text is null ? null : new DetailedTranscription(text);
    }

    /// <summary>Whether the STT engine is ready for transcription.</summary>
    bool IsReady { get; }

    /// <summary>
    /// The current language code used for transcription (e.g. "en", "ru", "auto").
    /// </summary>
    string Language => "en";

    /// <summary>
    /// Changes the language used for transcription at runtime.
    /// Not all engines support this; the default implementation is a no-op.
    /// </summary>
    /// <param name="language">ISO 639-1 language code, or "auto" for auto-detection.</param>
    void SetLanguage(string language) { }

    /// <summary>
    /// Languages supported by this STT engine. Returns an empty array if unknown.
    /// </summary>
    IReadOnlyList<string> SupportedLanguages => [];
}
