using System.Collections.Concurrent;
using Cortex.Contained.Channels.Discord;
using Cortex.Contained.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Channels.Discord.Tests;

/// <summary>
/// Barge-in coverage for <see cref="VoiceOutPipeline"/>: per-sentence frame
/// accounting (<c>StopForBargeInAsync</c>) and re-play (<c>ReenqueueSentence</c>).
/// Assertions stay robust to the real <see cref="StubTextToSpeech"/> (fixed PCM
/// payload per call, NOT 1 byte/char) and <see cref="RecordingAudioSink"/>
/// (records frames, no exact byte API) — ranges / Contains / non-null only.
/// </summary>
public class VoiceOutPipelineBargeInTests
{
    [Fact]
    public async Task StopForBargeIn_ReportsPlayedSentencesAndPartialRatio()
    {
        var tts = new StubTextToSpeech();
        // Bytes-per-sentence after mono→stereo upmix, expressed in 20 ms frames.
        // Gate engages one frame INTO the second sentence so the first sentence
        // is fully played and the second is genuinely in-flight at stop time.
        var framesPerSentence = (tts.BytesPerCall * 2) / (960 * 2 * 2);
        var sink = new GatedRecordingSink(gateAfterFrames: framesPerSentence + 1);
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        p.TryEnqueue("First. Second long sentence here.");
        await sink.WaitForGateAsync();

        var progress = await p.StopForBargeInAsync();

        Assert.NotNull(progress);
        Assert.Contains("First.", progress.FullyPlayedSentences);
        Assert.InRange(progress.InterruptedPlayedRatio, 0.0, 1.0);
    }

    [Fact]
    public async Task StopForBargeIn_AfterPriorTurnCompleted_ReportsOnlyCurrentTurnSentences()
    {
        // Regression: fullyPlayedSentences MUST reset at end-of-response. Before the
        // fix it accumulated every sentence played all session, so a barge-in in a
        // later turn reported the whole session (the 6.4k-char merged-history blob).
        var tts = new StubTextToSpeech();
        var framesPerSentence = (tts.BytesPerCall * 2) / (960 * 2 * 2);
        // Gate one frame into turn B's SECOND sentence: turn A (1 sentence) and
        // turn B's first sentence are both fully played by then.
        var sink = new GatedRecordingSink(gateAfterFrames: (framesPerSentence * 2) + 1);
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        // Turn A completes cleanly — its EndOfResponse must clear the accumulator.
        p.TryEnqueue("Alpha turn sentence.");
        p.MarkEndOfResponse();

        // Turn B is a fresh turn, interrupted mid second sentence.
        p.TryEnqueue("Bravo current sentence. Charlie second sentence here.");

        await sink.WaitForGateAsync();
        var progress = await p.StopForBargeInAsync();

        Assert.DoesNotContain("Alpha turn sentence.", progress.FullyPlayedSentences);
        Assert.Contains("Bravo current sentence.", progress.FullyPlayedSentences);
    }

    [Fact]
    public async Task ReenqueueSentence_PlaysItAgain()
    {
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech();
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        p.ReenqueueSentence("Replay me.");
        await WaitForFramesAsync(sink, atLeast: 1);

        Assert.NotEmpty(sink.Frames);
        Assert.True(tts.CallCount >= 1);
    }

    [Fact]
    public async Task StopForBargeIn_PipelineSurvives_NextReplyStillPlays()
    {
        // Regression (2026-05-16 gym outage): a "Real" barge-in must NOT
        // permanently kill voice-out. After StopForBargeInAsync the agent
        // still replies to the interruption — that reply must be synthesized
        // and played. Before the fix the producer/playback loops exited on
        // the bargeInStopped latch and this timed out.
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech();
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        p.TryEnqueue("First answer.");
        await WaitForFramesAsync(sink, atLeast: 1);
        var callsBefore = tts.CallCount;

        // User barges in; the current/queued audio is dropped.
        await p.StopForBargeInAsync();

        // Agent replies to the interruption — this MUST still play.
        p.TryEnqueue("Here is the reply to your interruption.");

        var deadline = DateTime.UtcNow.AddMilliseconds(5000);
        while (DateTime.UtcNow < deadline && tts.CallCount <= callsBefore)
        {
            await Task.Delay(20);
        }

        Assert.True(
            tts.CallCount > callsBefore,
            $"Pipeline died after barge-in: post-stop sentence never synthesized (calls={tts.CallCount}, before={callsBefore}).");
        await WaitForFramesAsync(sink, atLeast: 1);
        Assert.NotEmpty(sink.Frames);
    }

    [Fact]
    public async Task MarkEndOfResponse_FiresCompletionAfterPlayback()
    {
        // Per-answer "agent finished speaking" signal (fix for deferral #2:
        // Phase stuck Speaking → every answer mis-read as a barge-in).
        var completions = 0;
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech();
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(
            tts, sink, opts, NullLogger.Instance,
            onResponsePlaybackComplete: () => Interlocked.Increment(ref completions));

        p.TryEnqueue("This is the answer.");
        p.MarkEndOfResponse();
        await WaitForFramesAsync(sink, atLeast: 1);

        var deadline = DateTime.UtcNow.AddMilliseconds(5000);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref completions) == 0)
        {
            await Task.Delay(20);
        }

        Assert.Equal(1, Volatile.Read(ref completions));
    }

    [Fact]
    public async Task BargeIn_DiscardsPendingEndOfResponse_NoCompletion()
    {
        // If the user barges in before the response finished playing, the
        // pending end-of-response marker must be discarded — the arbiter
        // already resolved the turn via the interrupt path. Firing
        // OnAgentFinished here would wrongly flip Phase back to Listening.
        var completions = 0;
        var tts = new StubTextToSpeech();
        var framesPerSentence = (tts.BytesPerCall * 2) / (960 * 2 * 2);
        var sink = new GatedRecordingSink(gateAfterFrames: 1);
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(
            tts, sink, opts, NullLogger.Instance,
            onResponsePlaybackComplete: () => Interlocked.Increment(ref completions));

        p.TryEnqueue("First sentence. Second sentence here.");
        p.MarkEndOfResponse();
        await sink.WaitForGateAsync(); // playback parked mid first sentence; marker still queued

        await p.StopForBargeInAsync();
        await Task.Delay(300); // give the loops time to drop the stale marker

        Assert.Equal(0, Volatile.Read(ref completions));
    }

    [Fact]
    public async Task AllSynthesisFails_PlaysPreBakedNotice()
    {
        // Last-resort tier: when every sentence yields empty PCM (total
        // synthesis failure), the pre-baked trouble notice must play so the
        // user never hears dead air.
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech { BytesPerCall = 0 }; // synthesis always returns empty
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };
        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        p.TryEnqueue("This will fail to synthesize.");
        await WaitForFramesAsync(sink, atLeast: 1);

        Assert.NotEmpty(sink.Frames); // notice audio was framed and written
    }

    [Fact]
    public async Task AllSynthesisFails_NoticePlaysAtMostOncePerResponse()
    {
        // Two failing sentences in one response → the notice plays exactly once.
        // The notice is ~4.4s of 48 kHz stereo; count its frames so a second
        // notice would visibly roughly double the frame count.
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech { BytesPerCall = 0 };
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };

        var noticeMonoBytes = VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono.Length;
        var noticeStereoBytes = noticeMonoBytes * 2;
        var noticeFrames = (noticeStereoBytes + (960 * 2 * 2) - 1) / (960 * 2 * 2);

        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        // Two sentences in a single response, both fail to synthesize.
        p.TryEnqueue("First failing sentence. Second failing sentence.");
        await WaitForFramesAsync(sink, atLeast: noticeFrames);
        await Task.Delay(300); // allow any (erroneous) second notice to enqueue+play

        // Exactly one notice's worth of frames (allow the final padded frame).
        Assert.InRange(sink.Frames.Count, noticeFrames - 1, noticeFrames + 1);
        Assert.True(tts.CallCount >= 2, $"both sentences should have been attempted (calls={tts.CallCount})");
    }

    [Fact]
    public async Task AllSynthesisFails_NoticeResetsAcrossResponses()
    {
        // A fresh response can warn again: after MarkEndOfResponse resets the
        // per-response latch, a second failing response plays the notice again.
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech { BytesPerCall = 0 };
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };

        var noticeFrames = ((VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono.Length * 2) + (960 * 2 * 2) - 1) / (960 * 2 * 2);

        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        p.TryEnqueue("Response one fails.");
        p.MarkEndOfResponse();
        await WaitForFramesAsync(sink, atLeast: noticeFrames - 1);
        await Task.Delay(200);

        p.TryEnqueue("Response two also fails.");
        p.MarkEndOfResponse();

        // After the second response, roughly two notices' worth of frames.
        await WaitForFramesAsync(sink, atLeast: (noticeFrames * 2) - 2);
        Assert.True(sink.Frames.Count >= (noticeFrames * 2) - 2);
    }

    [Fact]
    public async Task AllSynthesisFails_NoticeSkipsResample_WhenSourceSampleRateNon48k()
    {
        // Locks the invariant: the trouble notice asset is ALWAYS 48 kHz mono
        // and must NOT be run through the SourceSampleRate-based resample. Here
        // SourceSampleRate is 24 kHz; a (wrong) 24k→48k upsample would roughly
        // DOUBLE the notice frame count. Assert the frame count matches the
        // UN-resampled 48k asset length — same as the 48k notice test.
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech { BytesPerCall = 0 }; // synthesis always returns empty
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = 24000 };

        var noticeMonoBytes = VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono.Length;
        var noticeStereoBytes = noticeMonoBytes * 2;
        var noticeFrames = (noticeStereoBytes + (960 * 2 * 2) - 1) / (960 * 2 * 2);

        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        p.TryEnqueue("This will fail to synthesize.");
        await WaitForFramesAsync(sink, atLeast: noticeFrames - 1);
        await Task.Delay(300); // allow any (erroneous) extra/doubled frames to land

        // Exactly one UN-resampled notice's worth of frames — NOT doubled.
        Assert.InRange(sink.Frames.Count, noticeFrames - 1, noticeFrames + 1);
    }

    [Fact]
    public async Task AllSynthesisFails_PlaysMaleNotice_WhenNoticeGenderMale()
    {
        // The pipeline constructed with VoiceGender.Male must play the MALE clip
        // on total synthesis failure — its frame count matches the male asset,
        // which is a different length than the default (female) clip.
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech { BytesPerCall = 0 };
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };

        var maleNoticeFrames = ((VoiceNoticeAudio.TroubleSpeakingMalePcm48kMono.Length * 2) + (960 * 2 * 2) - 1) / (960 * 2 * 2);

        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance, noticeGender: VoiceGender.Male);

        p.TryEnqueue("This will fail to synthesize.");
        await WaitForFramesAsync(sink, atLeast: maleNoticeFrames - 1);
        await Task.Delay(300); // allow any extra frames to land

        Assert.InRange(sink.Frames.Count, maleNoticeFrames - 1, maleNoticeFrames + 1);
    }

    [Fact]
    public async Task BargeIn_ResetsNoticeLatch_NextFailingResponseWarnsAgain()
    {
        // Response A fully fails → notice plays. A barge-in drains the in-band
        // EndOfResponse marker before the producer can reset the per-response
        // latch, so StopForBargeInAsync must reset it. Response B then also
        // fully fails and must warn again (roughly two notices' worth total).
        var sink = new RecordingAudioSink();
        var tts = new StubTextToSpeech { BytesPerCall = 0 };
        var opts = new VoiceOutOptions { OutputGain = 1.0f, SourceSampleRate = tts.OutputFormat.SampleRate };

        var noticeFrames = ((VoiceNoticeAudio.TroubleSpeakingFemalePcm48kMono.Length * 2) + (960 * 2 * 2) - 1) / (960 * 2 * 2);

        await using var p = new VoiceOutPipeline(tts, sink, opts, NullLogger.Instance);

        // Response A fails; notice plays. No MarkEndOfResponse — the barge-in
        // is what must clear the latch.
        p.TryEnqueue("Response A fails.");
        await WaitForFramesAsync(sink, atLeast: noticeFrames - 1);
        await Task.Delay(200);

        await p.StopForBargeInAsync();

        // Response B also fails — must warn again because the barge-in reset
        // the latch (without the fix the latch stays true and B is silent).
        p.TryEnqueue("Response B also fails.");
        await WaitForFramesAsync(sink, atLeast: (noticeFrames * 2) - 2);
        Assert.True(sink.Frames.Count >= (noticeFrames * 2) - 2);
    }

    private static async Task WaitForFramesAsync(RecordingAudioSink sink, int atLeast, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (sink.Frames.Count >= atLeast)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Waited {timeoutMs} ms for {atLeast} frames; got {sink.Frames.Count}.");
    }

    /// <summary>
    /// Recording sink that accepts frames until <c>gateAfterFrames</c> have been
    /// written, then blocks the next <see cref="WriteFrameAsync"/> indefinitely
    /// (until disposed). Lets a test stop playback at a deterministic point with
    /// at least one sentence already fully played.
    /// </summary>
    private sealed class GatedRecordingSink : IAudioOutSink
    {
        private readonly ConcurrentQueue<byte[]> frames = new();
        private readonly TaskCompletionSource gateReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int gateAfterFrames;
        private int written;
        private int flushCount;

        public GatedRecordingSink(int gateAfterFrames)
        {
            this.gateAfterFrames = gateAfterFrames;
        }

        public bool IsAvailable => true;

        public IReadOnlyCollection<byte[]> Frames => this.frames;

        public int FlushCount => Volatile.Read(ref this.flushCount);

        public Task WaitForGateAsync() => this.gateReached.Task;

        public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
        {
            var index = Interlocked.Increment(ref this.written);
            if (index >= this.gateAfterFrames)
            {
                this.gateReached.TrySetResult();
                // Hold the in-flight write open so the barge-in stop interrupts
                // a sentence that is genuinely still playing.
                await this.release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            this.frames.Enqueue(frame.ToArray());
        }

        public ValueTask FlushAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref this.flushCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.release.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
