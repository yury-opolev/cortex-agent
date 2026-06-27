namespace Cortex.Contained.Speech;

/// <summary>
/// TTS provider plugin interface. Each implementation represents a self-contained
/// speech synthesis engine that declares its voices, languages, and readiness status.
/// Voice is passed per-call (stateless) rather than via SetVoice (stateful).
/// </summary>
public interface ITtsProvider : IDisposable
{
    /// <summary>Unique provider name (e.g. "silero", "kokoro").</summary>
    string Name { get; }

    /// <summary>All voices this provider supports, with language and gender metadata.</summary>
    IReadOnlyList<TtsVoiceInfo> Voices { get; }

    /// <summary>Whether the model files are present and the provider is ready to synthesize.</summary>
    bool IsReady { get; }

    /// <summary>Human-readable status message (e.g. "Model ready" or "Model not found at ...").</summary>
    string StatusDetail { get; }

    /// <summary>Output audio format (sample rate, channels, bits per sample).</summary>
    AudioFormat OutputFormat { get; }

    /// <summary>Whether the provider supports streaming (sentence-by-sentence) synthesis.</summary>
    bool SupportsStreaming { get; }

    /// <summary>Whether this provider supports model download via <see cref="DownloadModelAsync"/>.</summary>
    bool CanDownloadModel => false;

    /// <summary>Human-readable label for the download button (e.g. "Download Silero v5 Russian (~100 MB)").</summary>
    string? DownloadLabel => null;

    /// <summary>
    /// Downloads and installs the model files for this provider.
    /// Only called when <see cref="CanDownloadModel"/> is true and <see cref="IsReady"/> is false.
    /// The provider already knows its target directory (set at construction time).
    /// </summary>
    /// <param name="httpClient">HTTP client for downloading files.</param>
    /// <param name="progress">Optional callback for download progress (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded and provider is now ready.</returns>
    Task<bool> DownloadModelAsync(
        HttpClient httpClient,
        Action<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>Synthesize text to PCM audio using the specified voice.</summary>
    Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken cancellationToken = default);

    /// <summary>Stream PCM audio chunks using the specified voice.</summary>
    IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string voiceName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata for a single TTS voice: name, language, gender, and optional description.
/// </summary>
public sealed record TtsVoiceInfo(
    string Name,
    string Language,
    VoiceGender Gender,
    string? Description = null);
