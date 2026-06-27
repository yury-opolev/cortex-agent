using System.Runtime.CompilerServices;

namespace Cortex.Contained.Speech.Tts;

/// <summary>
/// Placeholder provider that is never ready. Used when the configured engine
/// has no model files — prevents DI crashes while clearly reporting unavailability.
/// </summary>
public sealed class NullTtsProvider : ITtsProvider
{
    public NullTtsProvider(string engineName)
    {
        this.Name = engineName;
    }

    public string Name { get; }
    public IReadOnlyList<TtsVoiceInfo> Voices => [];
    public bool IsReady => false;
    public string StatusDetail => $"TTS engine '{this.Name}' model not found.";
    public AudioFormat OutputFormat => AudioFormat.Silero;
    public bool SupportsStreaming => false;

    public Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"TTS engine '{this.Name}' is not available (model not loaded).");

    public IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string voiceName, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"TTS engine '{this.Name}' is not available (model not loaded).");

    public void Dispose() { }
}
