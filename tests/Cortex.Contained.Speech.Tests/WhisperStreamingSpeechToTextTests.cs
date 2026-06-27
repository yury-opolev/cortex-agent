using Cortex.Contained.Speech.Stt;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests;

public class WhisperStreamingSpeechToTextTests
{
    [Fact]
    public void IsReady_WhenUnderlyingSttIsReady_ReturnsTrue()
    {
        var fakeStt = new ScriptedSttFake(isReady: true);

        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        Assert.True(sut.IsReady);
    }

    [Fact]
    public void GetPartialResult_BeforeAnyAudio_ReturnsEmptyString()
    {
        var fakeStt = new ScriptedSttFake();
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        var result = sut.GetPartialResult();

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task GetFinalResultAsync_AfterOneUtterance_ReturnsTranscribedText()
    {
        var fakeStt = new ScriptedSttFake(isReady: true, "hello world");
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(bytes: 4800));   // ~150ms of 16kHz 16-bit mono

        var final = await sut.GetFinalResultAsync();

        Assert.Equal("hello world", final);
        Assert.Equal(1, fakeStt.TranscribeCallCount);
    }

    [Fact]
    public async Task LocalAgreement_AfterSinglePass_CommitsNothing_ProvisionalHoldsFullOutput()
    {
        // After only one pass, nothing has "agreed across two passes" yet —
        // everything is still unstable.
        var fakeStt = new ScriptedSttFake(isReady: true, "hello world");
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();

        Assert.Equal("hello world", sut.GetPartialResult());
        Assert.Equal(string.Empty, sut.GetCommittedTextForTesting());
        Assert.Equal("hello world", sut.GetProvisionalTextForTesting());
    }

    [Fact]
    public async Task LocalAgreement_TwoIdenticalPasses_CommitsEverything()
    {
        var fakeStt = new ScriptedSttFake(isReady: true, "hello world", "hello world");
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();

        Assert.Equal("hello world", sut.GetCommittedTextForTesting());
        Assert.Equal(string.Empty, sut.GetProvisionalTextForTesting());
        Assert.Equal("hello world", sut.GetPartialResult());
    }

    [Fact]
    public async Task LocalAgreement_SecondPassExtendsFirst_CommitsCommonPrefix()
    {
        var fakeStt = new ScriptedSttFake(isReady: true, "hello world", "hello world today");
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();

        Assert.Equal("hello world", sut.GetCommittedTextForTesting());
        Assert.Equal("today", sut.GetProvisionalTextForTesting());
        Assert.Equal("hello world today", sut.GetPartialResult());
    }

    [Fact]
    public async Task LocalAgreement_PassesDiverge_CommitsOnlyCommonPrefix()
    {
        // Pass 1: "the quick brown fox"
        // Pass 2: "the quick red fox" — first 2 words agree, rest doesn't
        var fakeStt = new ScriptedSttFake(isReady: true, "the quick brown fox", "the quick red fox");
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();

        Assert.Equal("the quick", sut.GetCommittedTextForTesting());
        Assert.Equal("red fox", sut.GetProvisionalTextForTesting());
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("hello", "", "")]
    [InlineData("", "hello", "")]
    [InlineData("hello world", "hello world", "hello world")]
    [InlineData("hello world", "hello world today", "hello world")]
    [InlineData("Hello, world.", "hello world today", "hello world")]   // ignore case + trailing punctuation
    [InlineData("the quick brown fox", "the quick red fox", "the quick")]
    [InlineData("apple", "banana", "")]
    public void LongestCommonWordPrefix_Cases(string a, string b, string expectedPrefix)
    {
        // Note: the implementation returns tokens drawn from the second argument,
        // so expected strings should use that casing/punctuation where relevant.
        var result = WhisperStreamingSpeechToText.LongestCommonWordPrefix(a, b);

        // Assert the prefix *words* match (case-insensitive, punctuation-insensitive)
        // since that's what actually matters semantically.
        var expected = expectedPrefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var actual = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                string.Equals(
                    expected[i].Trim('.', ',', '!', '?'),
                    actual[i].Trim('.', ',', '!', '?'),
                    StringComparison.OrdinalIgnoreCase),
                $"word {i}: expected '{expected[i]}' got '{actual[i]}'");
        }
    }

    [Fact]
    public async Task Reset_ClearsAllState()
    {
        var fakeStt = new ScriptedSttFake(isReady: true, "hello world", "hello world");
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(4800));
        await sut.TranscribePendingAsync();

        Assert.Equal("hello world", sut.GetPartialResult());

        sut.Reset();

        Assert.Equal(string.Empty, sut.GetPartialResult());
        Assert.Equal(string.Empty, sut.GetCommittedTextForTesting());
        Assert.Equal(string.Empty, sut.GetProvisionalTextForTesting());
    }

    [Fact]
    public async Task GetFinalResultAsync_OnEmptyBuffer_ReturnsEmptyString()
    {
        var fakeStt = new ScriptedSttFake(isReady: true);
        using var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        var final = await sut.GetFinalResultAsync();

        Assert.Equal(string.Empty, final);
        Assert.Equal(0, fakeStt.TranscribeCallCount);
    }

    [Fact]
    public void Dispose_DoesNotDisposeInjectedBatchStt()
    {
        // Loose coupling: the streaming wrapper doesn't own the underlying STT.
        // Callers pass in a shared ISpeechToText and keep responsibility for its lifecycle.
        var fakeStt = new ScriptedSttFake(isReady: true);
        var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.Dispose();

        Assert.True(fakeStt.IsReady, "Underlying STT should still be ready — its lifetime is owned by the caller.");
    }

    [Fact]
    public async Task AcceptAudio_WhenBufferExceedsThreshold_TriggersBackgroundTranscription()
    {
        // Threshold 32 bytes → first AcceptAudio(32) should kick off a transcription.
        var fakeStt = new ScriptedSttFake(isReady: true, "hello");
        using var sut = new WhisperStreamingSpeechToText(
            fakeStt,
            NullLogger<WhisperStreamingSpeechToText>.Instance,
            autoTranscribeThresholdBytes: 32);

        sut.AcceptAudio(CreateDummyPcm(32));
        await sut.WaitForPendingTranscriptionAsync();

        Assert.Equal(1, fakeStt.TranscribeCallCount);
        Assert.Equal("hello", sut.GetPartialResult());
    }

    [Fact]
    public async Task AcceptAudio_WhenBufferBelowThreshold_DoesNotTrigger()
    {
        var fakeStt = new ScriptedSttFake(isReady: true, "hello");
        using var sut = new WhisperStreamingSpeechToText(
            fakeStt,
            NullLogger<WhisperStreamingSpeechToText>.Instance,
            autoTranscribeThresholdBytes: 32);

        sut.AcceptAudio(CreateDummyPcm(16));
        await sut.WaitForPendingTranscriptionAsync();

        Assert.Equal(0, fakeStt.TranscribeCallCount);
        Assert.Equal(string.Empty, sut.GetPartialResult());
    }

    [Fact]
    public async Task AcceptAudio_WhenAutoTriggerDisabled_NeverKicksOffTranscription()
    {
        var fakeStt = new ScriptedSttFake(isReady: true, "hello");
        using var sut = new WhisperStreamingSpeechToText(
            fakeStt,
            NullLogger<WhisperStreamingSpeechToText>.Instance,
            autoTranscribeThresholdBytes: 0);   // 0 = disabled

        sut.AcceptAudio(CreateDummyPcm(100_000));
        await sut.WaitForPendingTranscriptionAsync();

        Assert.Equal(0, fakeStt.TranscribeCallCount);
    }

    [Fact]
    public void AcceptAudio_AfterDispose_Throws()
    {
        var fakeStt = new ScriptedSttFake();
        var sut = new WhisperStreamingSpeechToText(fakeStt, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sut.AcceptAudio(CreateDummyPcm(16)));
    }

    private static byte[] CreateDummyPcm(int bytes)
    {
        // Non-zero bytes so the STT wrapper doesn't decide to skip silent audio
        // (our fake doesn't care about content, but future trimming might).
        var data = new byte[bytes];
        Array.Fill(data, (byte)0x10);
        return data;
    }

    // --- Test double: ISpeechToText that returns scripted transcription results
    //     in order, one per TranscribeAsync call. Keeps the streaming STT unit tests
    //     deterministic and decoupled from the real Whisper model. ---
    private sealed class ScriptedSttFake : ISpeechToText
    {
        private readonly Queue<string?> scriptedResults;

        public ScriptedSttFake(bool isReady = true, params string?[] scriptedResults)
        {
            this.IsReady = isReady;
            this.scriptedResults = new Queue<string?>(scriptedResults);
        }

        public bool IsReady { get; private set; }

        public int TranscribeCallCount { get; private set; }

        public Task<string?> TranscribeAsync(byte[] pcmData, CancellationToken cancellationToken = default)
        {
            this.TranscribeCallCount++;
            var next = this.scriptedResults.Count > 0 ? this.scriptedResults.Dequeue() : null;
            return Task.FromResult(next);
        }

        public void Dispose()
        {
            // We intentionally track but do not change IsReady on dispose —
            // tests that care about dispose behavior of the streaming wrapper
            // should not have their underlying fake be disposed by the wrapper.
            this.IsReady = false;
        }
    }
}
