namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Adapts an <see cref="ITtsProvider"/> to the legacy <see cref="ITextToSpeech"/> interface.
/// Used when a single engine is selected (not composite mode) so that consumers
/// (VoiceChannel, DiscordVoiceHandler) continue to work without changes.
/// </summary>
public sealed class TtsProviderAdapter : ITextToSpeech
{
    private readonly ITtsProvider provider;
    private volatile string currentVoice;

    public TtsProviderAdapter(ITtsProvider provider, string initialVoice)
    {
        this.provider = provider;
        this.currentVoice = initialVoice;
    }

    /// <inheritdoc />
    public AudioFormat OutputFormat => this.provider.OutputFormat;

    /// <inheritdoc />
    public string CurrentVoice => this.currentVoice;

    /// <inheritdoc />
    public bool SupportsStreaming => this.provider.SupportsStreaming;

    /// <inheritdoc />
    public Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default) =>
        this.provider.SynthesizeAsync(text, this.currentVoice, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string? languageHint = null, CancellationToken cancellationToken = default) =>
        this.provider.SynthesizeStreamingAsync(text, this.currentVoice, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableVoices() =>
        this.provider.Voices.Select(v => v.Name).ToList();

    /// <inheritdoc />
    public void SetVoice(string voiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voiceName);

        if (!this.provider.Voices.Any(v => string.Equals(v.Name, voiceName, StringComparison.OrdinalIgnoreCase)))
        {
            var available = string.Join(", ", this.provider.Voices.Select(v => v.Name));
            throw new ArgumentException(
                $"Voice '{voiceName}' not available in provider '{this.provider.Name}'. Available: {available}",
                nameof(voiceName));
        }

        this.currentVoice = voiceName;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
