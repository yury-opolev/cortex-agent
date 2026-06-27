namespace Cortex.Contained.Speech.Stt;

/// <summary>
/// Small dispatcher that decides between streaming STT (per-frame feed with
/// a LocalAgreement-stabilized final transcription) and batch STT (single
/// TranscribeAsync call on the full utterance). Callers hold one of these
/// per utterance and forward frames as audio arrives, then call
/// <see cref="FinalizeAsync"/> on silence / turn-end.
/// </summary>
/// <remarks>
/// The coordinator is intentionally dependency-injection friendly and free
/// of channel-specific concerns — both Discord voice and local voice can
/// reuse it.
/// </remarks>
public sealed class StreamingSttCoordinator
{
    private readonly ISpeechToText batchStt;
    private readonly IStreamingSpeechToText? streamingStt;

    /// <summary>
    /// Creates a coordinator.
    /// </summary>
    /// <param name="batchStt">Batch STT used when <paramref name="useStreaming"/>
    /// is false OR no streaming STT is available.</param>
    /// <param name="streaming">Optional streaming STT. May be null if
    /// streaming is not configured in the host.</param>
    /// <param name="useStreaming">Feature flag. Only takes effect when
    /// <paramref name="streaming"/> is also non-null — otherwise the
    /// coordinator silently falls back to batch.</param>
    public StreamingSttCoordinator(ISpeechToText batchStt, IStreamingSpeechToText? streaming, bool useStreaming)
    {
        this.batchStt = batchStt ?? throw new ArgumentNullException(nameof(batchStt));
        this.streamingStt = streaming;
        this.IsStreaming = useStreaming && streaming is not null;
    }

    /// <summary>
    /// True when the coordinator will route frames to the streaming backend.
    /// False when falling back to batch mode (either by config or because no
    /// streaming STT was provided).
    /// </summary>
    public bool IsStreaming { get; }

    /// <summary>
    /// Forward a single 16kHz mono 16-bit PCM frame to the streaming STT.
    /// No-op in batch mode so callers can invoke unconditionally.
    /// </summary>
    public void FeedFrame16k(ReadOnlySpan<byte> pcm16kMono)
    {
        if (this.IsStreaming)
        {
            this.streamingStt!.AcceptAudio(pcm16kMono);
        }
    }

    /// <summary>
    /// Finalize the current utterance. In streaming mode, asks the streaming
    /// STT for its final result and resets it for the next utterance. In batch
    /// mode, runs a single TranscribeAsync on <paramref name="pcm16kFullUtterance"/>.
    /// Returns null when no speech was recognized (unifying the two backends).
    /// </summary>
    public async Task<string?> FinalizeAsync(byte[] pcm16kFullUtterance, CancellationToken cancellationToken = default)
    {
        if (this.IsStreaming)
        {
            var text = await this.streamingStt!.GetFinalResultAsync(cancellationToken).ConfigureAwait(false);
            this.streamingStt.Reset();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return await this.batchStt.TranscribeAsync(pcm16kFullUtterance, cancellationToken).ConfigureAwait(false);
    }
}
