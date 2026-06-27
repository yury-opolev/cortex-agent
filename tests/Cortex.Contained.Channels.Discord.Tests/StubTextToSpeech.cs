using Cortex.Contained.Speech;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Minimal <see cref="ITextToSpeech"/> for pipeline tests. Returns a fixed
/// PCM payload per call after a configurable synthesis delay.
/// </summary>
internal sealed class StubTextToSpeech : ITextToSpeech
{
    private int callCount;

    public TimeSpan SynthesisDelay { get; set; } = TimeSpan.Zero;
    public int BytesPerCall { get; set; } = 3840 * 5; // 100 ms of 48 kHz stereo
    public Exception? ThrowOnCall { get; set; }
    public int CallCount => Volatile.Read(ref this.callCount);

    public AudioFormat OutputFormat => AudioFormat.Discord;
    public string CurrentVoice => "stub";
    public bool SupportsStreaming => false;

    public IReadOnlyList<string> GetAvailableVoices() => ["stub"];
    public void SetVoice(string voiceName) { }
    public void Dispose() { }

    public async Task<byte[]> SynthesizeAsync(string text, string? languageHint = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref this.callCount);
        if (this.ThrowOnCall is { } ex)
        {
            this.ThrowOnCall = null;
            throw ex;
        }
        if (this.SynthesisDelay > TimeSpan.Zero)
        {
            await Task.Delay(this.SynthesisDelay, cancellationToken).ConfigureAwait(false);
        }
        return new byte[this.BytesPerCall];
    }

    public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
        string text, string? languageHint = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var pcm = await this.SynthesizeAsync(text, languageHint, cancellationToken).ConfigureAwait(false);
        if (pcm.Length > 0)
        {
            yield return pcm;
        }
    }
}
