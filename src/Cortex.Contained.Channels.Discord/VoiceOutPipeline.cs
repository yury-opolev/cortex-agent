using System.Threading.Channels;
using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Channels.Discord;

/// <summary>
/// One voice-out session. Owns the sentence/audio queues, the producer/playback
/// background tasks, and the lifetime cancellation source. Created in
/// <see cref="DiscordVoiceHandler.JoinVoiceChannelAsync"/> after the audio sink
/// is ready; disposed in <see cref="DiscordVoiceHandler.LeaveVoiceChannelAsync"/>.
/// </summary>
/// <remarks>
/// Disposal cancels in-flight work and discards queued sentences and audio.
/// "Drop on disconnect" is enforced structurally — a disposed pipeline cannot
/// be reused. Each rejoin creates a new instance.
/// </remarks>
internal sealed partial class VoiceOutPipeline : IAsyncDisposable
{
    private const int OpusFrameSize = 960;
    private const int OpusFrameBytes = OpusFrameSize * 2 * 2; // 48 kHz stereo 16-bit, 20 ms

    private readonly ITextToSpeech tts;
    private readonly IAudioOutSink sink;
    private readonly VoiceOutOptions options;
    private readonly ILogger logger;

    // Bounded to cap memory under a runaway producer or a stalled playback sink.
    // One spoken response is at most a few dozen sentences, so 256 queued sentences
    // is far more headroom than any real response needs while still bounding growth.
    private const int SentenceQueueCapacity = 256;

    // Synthesized PCM is large (a sentence can be hundreds of KB), so the audio queue
    // is bounded more tightly. 64 chunks (~64 sentences of audio) keeps the producer
    // at most a bounded distance ahead of playback before WriteAsync applies
    // backpressure, preventing unbounded PCM accumulation if the sink stalls.
    private const int AudioQueueCapacity = 64;

    // SingleWriter is intentionally omitted: TryEnqueue may be called from any thread.
    // FullMode.Wait makes WriteAsync block (async) on a full queue; the sync TryWrite
    // callers (sentence queue) instead see TryWrite return false and drop gracefully,
    // which the enqueue paths already handle by logging an interrupted enqueue.
    private readonly Channel<SentenceMsg> sentenceQueue = Channel.CreateBounded<SentenceMsg>(
        new BoundedChannelOptions(SentenceQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly Channel<AudioItem> audioQueue = Channel.CreateBounded<AudioItem>(
        new BoundedChannelOptions(AudioQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    /// <summary>A queued sentence, or the end-of-response marker. The marker
    /// travels in-band through the same ordered queues so its completion fires
    /// only after every prior sentence's audio has actually played.
    /// <para>
    /// <paramref name="LanguageHint"/>, when non-null, is forwarded to
    /// <see cref="ITextToSpeech.SynthesizeAsync"/> on synthesis to route to a
    /// per-language voice (e.g. <c>"en"</c> for wizard prompts). When null,
    /// the producer falls back to <see cref="defaultLanguageHintProvider"/>
    /// (used for ordinary agent replies driven by the sticky channel store).
    /// </para></summary>
    private readonly record struct SentenceMsg(string? Sentence, bool EndOfResponse, string? LanguageHint = null);

    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Task producerTask;
    private readonly Task playbackTask;

    private int disposed; // 0 = no, 1 = yes

    // Last-resort safety tier: when ALL synthesis for a sentence fails (empty
    // pcm even after CompositeTtsEngine's retry), the producer plays a pre-baked
    // "trouble speaking" notice ONCE per response instead of dropping silently.
    // Reset on each end-of-response marker by the producer loop, and also from
    // StopForBargeInAsync (a barge-in drains the EndOfResponse marker before the
    // producer can reset it, so the reset is done there too). This is otherwise
    // a single-writer (producer loop) field; the barge-in path's write is a
    // benign cross-thread bool write the producer only reads — no torn read for
    // a bool — so plain (non-volatile) is sufficient.
    private bool noticePlayedThisResponse;

    // Per-sentence frame accounting for barge-in progress reporting.
    private readonly object progressLock = new();
    private string? currentSentence;
    private int currentSentenceTotalFrames;
    private int currentSentenceFramesWritten;
    private readonly List<string> fullyPlayedSentences = [];

    // Monotonic barge-in epoch. A barge-in increments it; the producer/playback
    // loops drop any work stamped with a stale epoch but KEEP RUNNING, so the
    // agent's reply to the interruption still plays. (Replaced a terminal
    // bargeInStopped latch that permanently killed voice-out — the 2026-05-16
    // gym outage: every agent reply after the first barge-in was generated but
    // never spoken.)
    private int bargeInEpoch;

    /// <summary>One synthesized sentence (or an end-of-response marker when
    /// <paramref name="EndOfResponse"/> is true): source text, playable PCM,
    /// frame count, and the barge-in epoch it was produced under.</summary>
    private sealed record AudioItem(string Sentence, byte[] Pcm, int TotalFrames, int Epoch, bool EndOfResponse = false);

    private readonly Action? onResponsePlaybackComplete;

    /// <summary>
    /// Provider for the default TTS language hint used when an enqueued
    /// sentence does not carry an explicit per-message hint. Resolved lazily
    /// inside the producer loop so the channel's sticky current language is
    /// read at synthesis time, not at enqueue time. <see langword="null"/>
    /// when language routing is not configured — synthesis then receives no
    /// hint and falls back to the TTS engine's default voice.
    /// </summary>
    private readonly Func<string?>? defaultLanguageHintProvider;

    // Voice gender used to pick the male/female variant of the last-resort
    // "trouble speaking" notice so it matches the agent's configured voice.
    private readonly VoiceGender noticeGender;

    public VoiceOutPipeline(
        ITextToSpeech tts,
        IAudioOutSink sink,
        VoiceOutOptions options,
        ILogger logger,
        Action? onResponsePlaybackComplete = null,
        Func<string?>? defaultLanguageHintProvider = null,
        VoiceGender noticeGender = VoiceGender.Female)
    {
        this.onResponsePlaybackComplete = onResponsePlaybackComplete;
        this.defaultLanguageHintProvider = defaultLanguageHintProvider;
        this.noticeGender = noticeGender;
        this.tts = tts;
        this.sink = sink;
        this.options = options;
        this.logger = logger;

        this.producerTask = Task.Run(() => this.ProducerLoopAsync(this.lifetimeCts.Token));
        this.playbackTask = Task.Run(() => this.PlaybackLoopAsync(this.lifetimeCts.Token));

        this.LogPipelineStarted();
    }

    /// <summary>
    /// Enqueue a text message. The message is sentence-chunked first; each sentence
    /// becomes one queue item. Returns false if the pipeline is disposing.
    /// </summary>
    /// <param name="text">The text to synthesize.</param>
    /// <param name="languageHint">Optional ISO 639-1 language code forwarded to
    /// the TTS engine for every sentence in this message (e.g. <c>"en"</c> for
    /// wizard prompts). When <see langword="null"/>, synthesis falls back to
    /// the pipeline's <c>defaultLanguageHintProvider</c> (typically the
    /// channel's sticky current language).</param>
    public bool TryEnqueue(string text, string? languageHint = null)
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var sentences = SentenceChunker.Split(text, this.options.MaxChunkChars);
        var queuedCount = 0;
        foreach (var sentence in sentences)
        {
            if (!this.sentenceQueue.Writer.TryWrite(new SentenceMsg(sentence, false, languageHint)))
            {
                this.LogEnqueueInterrupted(queuedCount, sentences.Count);
                return queuedCount > 0;
            }

            queuedCount++;
        }

        this.LogEnqueued(text.Length, sentences.Count);
        return true;
    }

    /// <summary>
    /// Mark the end of the agent's current response. The marker travels in-band
    /// after the last sentence; when playback reaches it (and no barge-in has
    /// superseded this response), <c>onResponsePlaybackComplete</c> fires —
    /// i.e. "the agent has finished speaking this answer". A barge-in drains /
    /// staleness-drops the marker, so completion does not fire on an interrupt.
    /// </summary>
    public void MarkEndOfResponse()
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            return;
        }

        // Sync caller — TryWrite. On the bounded queue (capacity 256) a full queue would
        // silently drop the marker and onResponsePlaybackComplete would never fire for
        // this response; log so the (practically unreachable) condition is observable.
        if (!this.sentenceQueue.Writer.TryWrite(new SentenceMsg(null, true)))
        {
            this.LogEndOfResponseMarkerDropped();
        }
    }

    /// <summary>
    /// Stop playback immediately for a barge-in and report what the user heard.
    /// Idempotent; the pipeline is single-use afterwards (re-enqueue clears it).
    /// </summary>
    public Task<PlaybackProgress> StopForBargeInAsync()
    {
        // New epoch: in-flight + queued audio is now stale and will be dropped
        // by the loops, but the loops stay alive for the agent's reply.
        Interlocked.Increment(ref this.bargeInEpoch);
        lock (this.progressLock)
        {
            string? interrupted = this.currentSentence;
            double ratio = (interrupted is not null && this.currentSentenceTotalFrames > 0)
                ? (double)this.currentSentenceFramesWritten / this.currentSentenceTotalFrames
                : 0.0;
            var played = this.fullyPlayedSentences.ToArray();

            // Reset the per-turn accounting now that it's been captured: the turn is
            // over (the agent's reply to the interruption is a new turn), so the next
            // barge-in must not re-report these sentences.
            this.fullyPlayedSentences.Clear();
            this.currentSentence = null;
            this.currentSentenceTotalFrames = 0;
            this.currentSentenceFramesWritten = 0;

            // Drain anything still queued — definitely not heard.
            while (this.audioQueue.Reader.TryRead(out _))
            {
            }

            while (this.sentenceQueue.Reader.TryRead(out _))
            {
            }

            // A new response always follows a barge-in; the drained EndOfResponse
            // marker will never reach the producer loop to reset this, so reset it
            // here — otherwise the last-resort notice could be suppressed on the
            // next fully-failing response.
            this.noticePlayedThisResponse = false;

            this.LogBargeInStopped(played.Length, interrupted is not null);
            return Task.FromResult(new PlaybackProgress(played, interrupted, ratio));
        }
    }

    /// <summary>Re-play a sentence from its start (backchannel resume).</summary>
    public void ReenqueueSentence(string sentence)
    {
        lock (this.progressLock)
        {
            this.fullyPlayedSentences.Clear();
            this.currentSentence = null;
        }

        // Produced under the current epoch; plays normally (backchannel resume).
        this.sentenceQueue.Writer.TryWrite(new SentenceMsg(sentence, false));
    }

    private async Task ProducerLoopAsync(CancellationToken ct)
    {
        var needsResample = this.options.SourceSampleRate != 48000;

        try
        {
            await foreach (var msg in this.sentenceQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                var epoch = Volatile.Read(ref this.bargeInEpoch);

                // End-of-response marker: forward it in-band (no synthesis) so
                // playback can fire completion strictly after the last sentence.
                if (msg.EndOfResponse)
                {
                    // A new response begins after this marker — let it warn again
                    // if its synthesis also fails entirely.
                    this.noticePlayedThisResponse = false;
                    try
                    {
                        await this.audioQueue.Writer.WriteAsync(
                            new AudioItem(string.Empty, [], 0, epoch, EndOfResponse: true), ct).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    continue;
                }

                var sentence = msg.Sentence!;
                var hint = msg.LanguageHint ?? this.defaultLanguageHintProvider?.Invoke();

                try
                {
                    var synthStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    var pcm = await this.tts.SynthesizeAsync(sentence, languageHint: hint, cancellationToken: ct).ConfigureAwait(false);
                    var synthMs = (int)System.Diagnostics.Stopwatch.GetElapsedTime(synthStart).TotalMilliseconds;
                    if (pcm.Length == 0)
                    {
                        // Total synthesis failure (CompositeTtsEngine already
                        // retried the default-language voice). Last-resort tier:
                        // play the pre-baked notice once per response so the user
                        // never hears dead air. The notice asset is ALWAYS 48 kHz
                        // mono regardless of options.SourceSampleRate, so it must
                        // NOT be resampled — pass needsResample: false.
                        var notice = VoiceNoticeAudio.TroubleSpeaking(this.noticeGender);
                        if (!this.noticePlayedThisResponse && notice.Length > 0
                            && Volatile.Read(ref this.bargeInEpoch) == epoch)
                        {
                            if (!await this.EnqueuePcm48kMonoAsync(sentence, notice, epoch, needsResample: false, ct).ConfigureAwait(false))
                            {
                                break;
                            }

                            this.noticePlayedThisResponse = true;
                            this.LogNoticePlayed();
                        }

                        continue;
                    }

                    // A barge-in landed while we were synthesizing — this audio
                    // is stale; drop it and keep serving newer sentences.
                    if (Volatile.Read(ref this.bargeInEpoch) != epoch)
                    {
                        continue;
                    }

                    var pcm48kStereo = ToStereo48k(pcm, needsResample, this.options.SourceSampleRate, this.options.OutputGain);

                    var totalFrames = (pcm48kStereo.Length + OpusFrameBytes - 1) / OpusFrameBytes;
                    try
                    {
                        await this.audioQueue.Writer.WriteAsync(
                            new AudioItem(sentence, pcm48kStereo, totalFrames, epoch), ct).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    // audioMs is the playable duration (20 ms per Opus frame). When
                    // synthMs approaches or exceeds audioMs the producer cannot stay
                    // ahead of playback → audible inter-sentence gaps. Logged at
                    // Information so the synth-vs-playback margin is visible without
                    // enabling Debug.
                    this.LogSentenceSynthesized(sentence.Length, pcm48kStereo.Length, synthMs, totalFrames * 20);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    this.LogProducerError(ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        finally
        {
            this.audioQueue.Writer.TryComplete();
            this.LogProducerExited();
        }
    }

    /// <summary>
    /// Convert a mono PCM buffer to the playable 48 kHz stereo form: resample to
    /// 48 kHz (skipped when <paramref name="needsResample"/> is false), upmix to
    /// stereo, and apply output gain.
    /// </summary>
    private static byte[] ToStereo48k(byte[] monoPcm, bool needsResample, int sourceSampleRate, float gain)
    {
        var pcm48kMono = needsResample
            ? AudioConverter.Resample(monoPcm, sourceSampleRate, 48000)
            : monoPcm;

        var pcm48kStereo = AudioConverter.MonoToStereo(pcm48kMono);
        AudioConverter.ApplyGain(pcm48kStereo, gain);
        return pcm48kStereo;
    }

    /// <summary>
    /// Frame a mono PCM buffer and enqueue it as an <see cref="AudioItem"/> under
    /// <paramref name="epoch"/>. Awaits on a full bounded queue (backpressure).
    /// Returns false only when the audio queue is closed (caller should exit the
    /// producer loop).
    /// </summary>
    private async Task<bool> EnqueuePcm48kMonoAsync(string sentence, byte[] monoPcm, int epoch, bool needsResample, CancellationToken ct)
    {
        var pcm48kStereo = ToStereo48k(monoPcm, needsResample, this.options.SourceSampleRate, this.options.OutputGain);
        var totalFrames = (pcm48kStereo.Length + OpusFrameBytes - 1) / OpusFrameBytes;
        try
        {
            await this.audioQueue.Writer.WriteAsync(
                new AudioItem(sentence, pcm48kStereo, totalFrames, epoch), ct).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in this.audioQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                // Audio synthesized before a barge-in is stale — drop it but
                // keep the loop alive for the agent's reply to the interruption.
                if (item.Epoch != Volatile.Read(ref this.bargeInEpoch))
                {
                    continue;
                }

                // End-of-response marker reached under the current epoch: every
                // prior sentence's audio has played → the agent has finished
                // speaking this answer. (A barge-in would have bumped the epoch
                // and dropped this above, so this never fires on an interrupt.)
                if (item.EndOfResponse)
                {
                    // Turn finished cleanly: reset the per-turn played-sentence
                    // accounting so the NEXT turn's barge-in only ever reports its
                    // own sentences. Without this the list grew for the whole
                    // session and a later barge-in recorded every sentence ever
                    // played as one giant interrupted turn.
                    lock (this.progressLock)
                    {
                        this.fullyPlayedSentences.Clear();
                        this.currentSentence = null;
                        this.currentSentenceTotalFrames = 0;
                        this.currentSentenceFramesWritten = 0;
                    }

                    this.LogResponsePlaybackComplete();
                    this.onResponsePlaybackComplete?.Invoke();
                    continue;
                }

                var epoch = item.Epoch;
                var chunk = item.Pcm;
                if (!this.sink.IsAvailable)
                {
                    this.LogPlaybackSinkUnavailable(chunk.Length);
                    continue;
                }

                lock (this.progressLock)
                {
                    this.currentSentence = item.Sentence;
                    this.currentSentenceTotalFrames = item.TotalFrames;
                    this.currentSentenceFramesWritten = 0;
                }

                var disposing = false;   // ct/disposal → exit the loop
                var superseded = false;  // barge-in → abandon this chunk, stay alive
                var offset = 0;
                while (offset + OpusFrameBytes <= chunk.Length)
                {
                    if (ct.IsCancellationRequested)
                    {
                        disposing = true;
                        break;
                    }

                    if (Volatile.Read(ref this.bargeInEpoch) != epoch || !this.sink.IsAvailable)
                    {
                        superseded = true;
                        break;
                    }

                    try
                    {
                        await this.sink.WriteFrameAsync(
                            new ReadOnlyMemory<byte>(chunk, offset, OpusFrameBytes), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        disposing = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        this.LogPlaybackError(ex.Message);
                        break;
                    }

                    lock (this.progressLock)
                    {
                        this.currentSentenceFramesWritten++;
                    }

                    offset += OpusFrameBytes;
                }
                if (disposing) { break; }
                if (superseded) { continue; }

                // Pad and write the last partial frame if any.
                if (offset < chunk.Length
                    && Volatile.Read(ref this.bargeInEpoch) == epoch
                    && this.sink.IsAvailable)
                {
                    var paddedFrame = new byte[OpusFrameBytes];
                    Buffer.BlockCopy(chunk, offset, paddedFrame, 0, chunk.Length - offset);
                    try
                    {
                        await this.sink.WriteFrameAsync(paddedFrame, ct).ConfigureAwait(false);
                        lock (this.progressLock)
                        {
                            this.currentSentenceFramesWritten++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        disposing = true;
                    }
                    catch (Exception ex)
                    {
                        this.LogPlaybackError(ex.Message);
                    }
                }
                if (disposing) { break; }

                if (this.sink.IsAvailable)
                {
                    try
                    {
                        await this.sink.FlushAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        disposing = true;
                    }
                    catch (Exception ex)
                    {
                        this.LogPlaybackError(ex.Message);
                    }
                }
                if (disposing) { break; }

                // Sentence played to completion without interruption.
                lock (this.progressLock)
                {
                    this.fullyPlayedSentences.Add(item.Sentence);
                    this.currentSentence = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on disposal.
        }
        finally
        {
            this.LogPlaybackExited();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        var droppedSentences = 0;
        while (this.sentenceQueue.Reader.TryRead(out _))
        {
            droppedSentences++;
        }

        var droppedChunks = 0;
        while (this.audioQueue.Reader.TryRead(out _))
        {
            droppedChunks++;
        }

        this.lifetimeCts.Cancel();
        this.sentenceQueue.Writer.TryComplete();
        this.audioQueue.Writer.TryComplete();

        try
        {
            await this.producerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            this.LogProducerError(ex.Message);
        }

        try
        {
            await this.playbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            this.LogPlaybackError(ex.Message);
        }

        // Dispose the sink AFTER both loops have stopped, so no in-flight write hits a disposed stream.
        try
        {
            await this.sink.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            this.LogPlaybackError(ex.Message);
        }

        this.lifetimeCts.Dispose();
        this.LogPipelineDisposed(droppedSentences, droppedChunks);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: pipeline started")]
    private partial void LogPipelineStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: pipeline disposed (dropped {DroppedSentences} sentences, {DroppedChunks} audio chunks)")]
    private partial void LogPipelineDisposed(int droppedSentences, int droppedChunks);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: enqueued {TextLength} chars as {SentenceCount} sentences")]
    private partial void LogEnqueued(int textLength, int sentenceCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: barge-in stop ({PlayedSentences} played, partial={HadPartial})")]
    private partial void LogBargeInStopped(int playedSentences, bool hadPartial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-out: enqueue interrupted ({QueuedCount}/{TotalCount} sentences queued before channel closure)")]
    private partial void LogEnqueueInterrupted(int queuedCount, int totalCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-out: end-of-response marker dropped (sentence queue full) — playback completion will not fire for this response")]
    private partial void LogEndOfResponseMarkerDropped();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: sentence synthesized {SentenceChars} chars -> {PcmBytes} PCM bytes in {SynthMs} ms (audio {AudioMs} ms)")]
    private partial void LogSentenceSynthesized(int sentenceChars, int pcmBytes, int synthMs, int audioMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: producer loop exited")]
    private partial void LogProducerExited();

    [LoggerMessage(Level = LogLevel.Error, Message = "voice-out: producer error: {ErrorMessage}")]
    private partial void LogProducerError(string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-out: all synthesis failed for a sentence; played pre-baked trouble notice")]
    private partial void LogNoticePlayed();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: playback loop exited")]
    private partial void LogPlaybackExited();

    [LoggerMessage(Level = LogLevel.Information, Message = "voice-out: response playback complete (agent finished speaking)")]
    private partial void LogResponsePlaybackComplete();

    [LoggerMessage(Level = LogLevel.Warning, Message = "voice-out: sink unavailable, dropped {ChunkBytes} byte chunk")]
    private partial void LogPlaybackSinkUnavailable(int chunkBytes);

    [LoggerMessage(Level = LogLevel.Error, Message = "voice-out: playback error: {ErrorMessage}")]
    private partial void LogPlaybackError(string errorMessage);
}
