using Cortex.Contained.Speech.Stt;

namespace Cortex.Contained.Speech.Tests;

public class StreamingSttCoordinatorTests
{
    [Fact]
    public void IsStreaming_WithStreamingEnabledAndProvided_ReturnsTrue()
    {
        var batch = Substitute.For<ISpeechToText>();
        var streaming = new StreamingSttFake();

        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: true);

        Assert.True(sut.IsStreaming);
    }

    [Fact]
    public void IsStreaming_WithStreamingEnabledButNullStreamer_ReturnsFalse()
    {
        // If the feature flag is on but no streaming STT was provided,
        // the coordinator must fall back to batch mode — not crash.
        var batch = Substitute.For<ISpeechToText>();

        var sut = new StreamingSttCoordinator(batch, streaming: null, useStreaming: true);

        Assert.False(sut.IsStreaming);
    }

    [Fact]
    public void IsStreaming_WithStreamingDisabled_ReturnsFalse()
    {
        var batch = Substitute.For<ISpeechToText>();
        var streaming = new StreamingSttFake();

        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: false);

        Assert.False(sut.IsStreaming);
    }

    [Fact]
    public void FeedFrame16k_WhenStreaming_ForwardsToStreamingStt()
    {
        var batch = Substitute.For<ISpeechToText>();
        var streaming = new StreamingSttFake();
        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: true);

        var frame = new byte[] { 1, 2, 3, 4, 5, 6 };
        sut.FeedFrame16k(frame);

        Assert.Equal(1, streaming.AcceptAudioCallCount);
        Assert.Equal(6, streaming.TotalBytesAccepted);
    }

    [Fact]
    public void FeedFrame16k_WhenNotStreaming_DoesNotCallStreamingStt()
    {
        var batch = Substitute.For<ISpeechToText>();
        var streaming = new StreamingSttFake();
        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: false);

        sut.FeedFrame16k(new byte[] { 1, 2, 3 });

        Assert.Equal(0, streaming.AcceptAudioCallCount);
    }

    [Fact]
    public async Task FinalizeAsync_WhenStreaming_CallsStreamingGetFinalThenReset()
    {
        var batch = Substitute.For<ISpeechToText>();
        var streaming = new StreamingSttFake { FinalResult = "hello world" };
        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: true);

        var result = await sut.FinalizeAsync(new byte[] { 1, 2 });

        Assert.Equal("hello world", result);
        Assert.Equal(1, streaming.GetFinalResultCallCount);
        Assert.Equal(1, streaming.ResetCallCount);
        await batch.DidNotReceive().TranscribeAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FinalizeAsync_WhenStreamingReturnsEmpty_ReturnsNull()
    {
        // Unify the "no speech" signal with batch mode (null).
        var batch = Substitute.For<ISpeechToText>();
        var streaming = new StreamingSttFake { FinalResult = string.Empty };
        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: true);

        var result = await sut.FinalizeAsync(new byte[] { 1 });

        Assert.Null(result);
    }

    [Fact]
    public async Task FinalizeAsync_WhenNotStreaming_CallsBatchTranscribe()
    {
        var batch = Substitute.For<ISpeechToText>();
        batch.TranscribeAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns("batch result");
        var streaming = new StreamingSttFake();
        var sut = new StreamingSttCoordinator(batch, streaming, useStreaming: false);

        var pcm = new byte[] { 9, 9, 9 };
        var result = await sut.FinalizeAsync(pcm);

        Assert.Equal("batch result", result);
        await batch.Received(1).TranscribeAsync(pcm, Arg.Any<CancellationToken>());
        Assert.Equal(0, streaming.GetFinalResultCallCount);
    }

    // Hand-rolled fake — NSubstitute can't intercept ReadOnlySpan<byte> parameters
    // because ref structs can't be used as generic type arguments.
    private sealed class StreamingSttFake : IStreamingSpeechToText
    {
        public bool IsReady { get; set; } = true;

        public int AcceptAudioCallCount { get; private set; }

        public int TotalBytesAccepted { get; private set; }

        public int GetFinalResultCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public string FinalResult { get; set; } = string.Empty;

        public void AcceptAudio(ReadOnlySpan<byte> pcm16kMono)
        {
            this.AcceptAudioCallCount++;
            this.TotalBytesAccepted += pcm16kMono.Length;
        }

        public string GetPartialResult() => string.Empty;

        public Task<string> GetFinalResultAsync(CancellationToken cancellationToken = default)
        {
            this.GetFinalResultCallCount++;
            return Task.FromResult(this.FinalResult);
        }

        public void Reset() => this.ResetCallCount++;

        public void Dispose()
        {
        }
    }
}
