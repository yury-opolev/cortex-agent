using System.Runtime.CompilerServices;

namespace Cortex.Contained.Speech;

/// <summary>
/// Text-to-speech engine. Converts text to audio PCM data.
/// </summary>
public interface ITextToSpeech : IDisposable
{
    /// <summary>
    /// Synthesize text to audio.
    /// </summary>
    /// <param name="text">Text to speak.</param>
    /// <param name="languageHint">
    /// ISO 639-1 code from a higher layer (e.g. the channel's sticky current language).
    /// When non-null, the composite engine routes to the configured voice for that language
    /// directly — bypassing per-sentence Lingua detection. Single-language engines ignore
    /// this parameter.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Audio data in the format specified by <see cref="OutputFormat"/>.</returns>
    Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synthesize text to audio in a streaming fashion, yielding PCM chunks as each
    /// text segment is synthesized. Implementations that support streaming can begin
    /// returning audio before the entire text has been processed, reducing time-to-first-audio.
    /// </summary>
    /// <param name="text">Text to speak.</param>
    /// <param name="languageHint">
    /// ISO 639-1 code from a higher layer (e.g. the channel's sticky current language).
    /// When non-null, the composite engine routes to the configured voice for that language
    /// directly — bypassing per-sentence Lingua detection. Single-language engines ignore
    /// this parameter.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// An async enumerable of PCM byte chunks in the format specified by <see cref="OutputFormat"/>.
    /// Each chunk represents a complete text segment (e.g. a sentence).
    /// </returns>
    IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default)
    {
        // Default implementation: synthesize the full text and yield it as a single chunk.
        return SynthesizeStreamingFallback(this, text, languageHint, cancellationToken);
    }

    /// <summary>
    /// Whether this TTS engine supports true streaming synthesis (segment-by-segment).
    /// When <c>false</c>, <see cref="SynthesizeStreamingAsync"/> falls back to
    /// synthesizing the full text as a single chunk.
    /// </summary>
    bool SupportsStreaming => false;

    /// <summary>Get available TTS voices.</summary>
    IReadOnlyList<string> GetAvailableVoices();

    /// <summary>Gets the name of the currently active voice.</summary>
    string CurrentVoice { get; }

    /// <summary>
    /// Change the active TTS voice at runtime.
    /// </summary>
    /// <param name="voiceName">Voice name (must be one of <see cref="GetAvailableVoices"/>).</param>
    void SetVoice(string voiceName);

    /// <summary>The audio format of the output from <see cref="SynthesizeAsync"/>.</summary>
    AudioFormat OutputFormat { get; }

    /// <summary>
    /// Default streaming fallback: synthesize the full text and yield as a single chunk.
    /// Static async-iterator helper because C# does not allow <c>yield return</c>
    /// inside a default interface method body.
    /// </summary>
    static async IAsyncEnumerable<byte[]> SynthesizeStreamingFallback(
        ITextToSpeech tts, string text, string? languageHint, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pcmData = await tts.SynthesizeAsync(text, languageHint, cancellationToken).ConfigureAwait(false);
        yield return pcmData;
    }
}
