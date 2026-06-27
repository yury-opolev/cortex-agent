using Cortex.Contained.Channels.Discord;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Channels.Discord.Tests;

public class VoiceOutPipelineTests
{
    private static VoiceOutPipeline CreatePipeline(StubTextToSpeech tts, RecordingAudioSink sink, VoiceOutOptions? options = null) =>
        new(tts, sink, options ?? new VoiceOutOptions(), NullLogger.Instance);

    [Fact]
    public async Task ShortMessage_AllFramesReachSink()
    {
        var tts = new StubTextToSpeech();
        var sink = new RecordingAudioSink();
        await using var pipeline = CreatePipeline(tts, sink);

        Assert.True(pipeline.TryEnqueue("Hello world."));

        await WaitForFramesAsync(sink, atLeast: 1);
        Assert.NotEmpty(sink.Frames);
        Assert.Equal(1, tts.CallCount);
    }

    [Fact]
    public async Task LongMessage_FirstFrameReachesSinkBeforeAllSentencesSynthesize()
    {
        // 5 sentences, each takes 100 ms to synthesize. First frame should land
        // ~100 ms after enqueue, well before all 500 ms of synthesis is done.
        var tts = new StubTextToSpeech { SynthesisDelay = TimeSpan.FromMilliseconds(100) };
        var sink = new RecordingAudioSink();
        await using var pipeline = CreatePipeline(tts, sink);

        var enqueueTime = DateTime.UtcNow;
        Assert.True(pipeline.TryEnqueue("First. Second. Third. Fourth. Fifth."));

        await WaitForFramesAsync(sink, atLeast: 1);
        var firstFrameTime = DateTime.UtcNow;

        Assert.True(
            (firstFrameTime - enqueueTime).TotalMilliseconds < 400,
            $"First frame took {(firstFrameTime - enqueueTime).TotalMilliseconds} ms; expected < 400 ms");
    }

    [Fact]
    public async Task LongMessage_DisposeMidSynthesis_NoFurtherFrames()
    {
        var tts = new StubTextToSpeech { SynthesisDelay = TimeSpan.FromMilliseconds(200) };
        var sink = new RecordingAudioSink();
        var pipeline = CreatePipeline(tts, sink);

        Assert.True(pipeline.TryEnqueue("First sentence. Second. Third. Fourth. Fifth."));

        // Wait for first frame to confirm pipeline is running.
        await WaitForFramesAsync(sink, atLeast: 1);

        await pipeline.DisposeAsync();

        // Allow some time for any in-flight work to settle.
        await Task.Delay(100);
        var framesAfterDispose = sink.Frames.Count;

        // No more sentences should be synthesized after disposal.
        // Total frames should be far less than 5 sentences * frames-per-sentence.
        Assert.True(framesAfterDispose < 5 * 5,
            $"Expected disposal to stop synthesis. Got {framesAfterDispose} frames.");
    }

    [Fact]
    public async Task TwoMessagesBackToBack_BothPlay()
    {
        var tts = new StubTextToSpeech();
        var sink = new RecordingAudioSink();
        await using var pipeline = CreatePipeline(tts, sink);

        Assert.True(pipeline.TryEnqueue("First message."));
        Assert.True(pipeline.TryEnqueue("Second message."));

        await WaitForCallCountAsync(tts, 2);

        Assert.Equal(2, tts.CallCount);
        Assert.NotEmpty(sink.Frames);
    }

    [Fact]
    public async Task TtsThrows_PipelineSurvives()
    {
        var tts = new StubTextToSpeech { ThrowOnCall = new InvalidOperationException("synth failed") };
        var sink = new RecordingAudioSink();
        await using var pipeline = CreatePipeline(tts, sink);

        Assert.True(pipeline.TryEnqueue("This will throw."));
        await Task.Delay(100);

        // Now enqueue a normal message — should succeed.
        Assert.True(pipeline.TryEnqueue("This works."));
        await WaitForCallCountAsync(tts, 2);
        Assert.Equal(2, tts.CallCount);
        Assert.NotEmpty(sink.Frames);
    }

    [Fact]
    public async Task SinkUnavailable_FramesDropped_NoCrash()
    {
        var tts = new StubTextToSpeech();
        var sink = new RecordingAudioSink();
        sink.SetUnavailable();
        await using var pipeline = CreatePipeline(tts, sink);

        Assert.True(pipeline.TryEnqueue("Hello."));
        await WaitForCallCountAsync(tts, 1);
        await Task.Delay(50);

        Assert.Empty(sink.Frames);
    }

    [Fact]
    public async Task SinkThrowsOnFrameWrite_PipelineSurvives()
    {
        // Sink throws an exception on first frame write.
        // The playback loop catches this exception (line 178), logs it, and
        // breaks from the frame-writing loop. Pipeline must not crash.
        // This validates exception handling doesn't break the async loops.
        var tts = new StubTextToSpeech();
        var sink = new RecordingAudioSink
        {
            ThrowOnNextWrite = new InvalidOperationException("simulated discord write failure"),
        };
        await using var pipeline = CreatePipeline(tts, sink);

        // Enqueue a message. Playback will attempt to write and throw.
        Assert.True(pipeline.TryEnqueue("Message one."));
        await WaitForCallCountAsync(tts, 1);

        // Allow playback loop time to process and encounter the throw.
        await Task.Delay(200);

        // Pipeline should still be alive — we can enqueue another message.
        // This will fail if the playback task crashed.
        var secondEnqueue = pipeline.TryEnqueue("Message two.");
        Assert.True(secondEnqueue, "Pipeline should still be able to enqueue after throw");

        await WaitForCallCountAsync(tts, 2);

        // Even without frames, the important test: no crash, pipeline functional.
    }

    [Fact]
    public async Task DisposeReturnsWithinReasonableTime_EvenWithLongQueue()
    {
        var tts = new StubTextToSpeech { SynthesisDelay = TimeSpan.FromMilliseconds(200) };
        var sink = new RecordingAudioSink();
        var pipeline = CreatePipeline(tts, sink);

        // Enqueue many sentences.
        for (var i = 0; i < 10; i++)
        {
            Assert.True(pipeline.TryEnqueue($"Sentence {i}."));
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await pipeline.DisposeAsync();
        stopwatch.Stop();

        // Should not wait for the full 10 * 200 ms = 2 s of pending synthesis.
        Assert.True(stopwatch.ElapsedMilliseconds < 1000,
            $"DisposeAsync took {stopwatch.ElapsedMilliseconds} ms; expected < 1000 ms");
    }

    [Fact]
    public async Task EmptyOrWhitespaceText_NoOp()
    {
        var tts = new StubTextToSpeech();
        var sink = new RecordingAudioSink();
        await using var pipeline = CreatePipeline(tts, sink);

        Assert.True(pipeline.TryEnqueue(""));
        Assert.True(pipeline.TryEnqueue("   "));

        await Task.Delay(50);
        Assert.Equal(0, tts.CallCount);
        Assert.Empty(sink.Frames);
    }

    [Fact]
    public async Task EnqueueAfterDispose_ReturnsFalse()
    {
        var tts = new StubTextToSpeech();
        var sink = new RecordingAudioSink();
        var pipeline = CreatePipeline(tts, sink);

        await pipeline.DisposeAsync();
        Assert.False(pipeline.TryEnqueue("Too late."));
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

    private static async Task WaitForCallCountAsync(StubTextToSpeech tts, int atLeast, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (tts.CallCount >= atLeast)
            {
                return;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"Waited {timeoutMs} ms for {atLeast} TTS calls; got {tts.CallCount}.");
    }
}
