using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Stt;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests;

/// <summary>
/// Tests for the trim-by-committed-token-timestamp behaviour of
/// <see cref="WhisperStreamingSpeechToText"/>. Uses <see cref="DetailedSttFake"/>
/// — a scripted ISpeechToText that overrides TranscribeDetailedAsync directly,
/// so we can return token-level timestamps for the streaming wrapper to use.
/// </summary>
public class WhisperStreamingSpeechToTextTrimTests
{
    private const int BytesPerSecond = 32_000; // 16kHz × 2 bytes/sample

    [Fact]
    public async Task FirstPass_NoCommits_PromptIsNull_FullAudioForwarded()
    {
        // Arrange: scripted single response, but only one pass — LA-2 needs two
        // matching passes to commit anything, so cursor stays at 0 and the
        // committed accumulator stays empty.
        var fake = new DetailedSttFake(
            new DetailedTranscription("hello", [new("hello", 0, 500)]));

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));   // 1s of audio

        // Act
        await sut.TranscribePendingAsync();

        // Assert
        Assert.Single(fake.Calls);
        Assert.Null(fake.Calls[0].Prompt);
        Assert.Equal(BytesPerSecond, fake.Calls[0].Audio.Length); // full buffer, no trim
        Assert.Equal(0, sut.GetBytesCommittedThroughForTesting());
        Assert.Equal(string.Empty, sut.GetCommittedAccumulatorForTesting());
    }

    [Fact]
    public async Task TwoMatchingPasses_CommitsAdvanceCursorAndAccumulator()
    {
        // Pass 1: Whisper sees 1s audio, returns "hello" with token end at 500ms.
        // Pass 2: Whisper sees 2s audio, returns "hello world" with tokens
        //         (hello 0-500, world 500-1000). LA-2 finds common prefix "hello",
        //         commits it, advances cursor by 500ms = 16000 bytes.
        var fake = new DetailedSttFake(
            new DetailedTranscription("hello",       [new("hello", 0, 500)]),
            new DetailedTranscription("hello world", [new("hello", 0, 500), new(" world", 500, 1000)]));

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
        await sut.TranscribePendingAsync();

        Assert.Equal(2, fake.Calls.Count);
        // Cursor advanced by end-of-"hello" token = 500ms = 16000 bytes.
        Assert.Equal(BytesPerSecond / 2, sut.GetBytesCommittedThroughForTesting());
        // Accumulator captured the commit.
        Assert.Equal("hello", sut.GetCommittedAccumulatorForTesting());
        // Partial = accumulator + provisional suffix.
        Assert.Equal("hello world", sut.GetPartialResult());
    }

    [Fact]
    public async Task ThirdPass_AfterCommit_ReceivesSlicedAudioAndPrompt()
    {
        // Replays the C3 scenario, then adds a third pass with more audio and
        // confirms the third call's audio is the post-cursor slice (not the
        // full buffer) and the prompt is the previously-committed text.
        var fake = new DetailedSttFake(
            new DetailedTranscription("hello",       [new("hello", 0, 500)]),
            new DetailedTranscription("hello world", [new("hello", 0, 500), new(" world", 500, 1000)]),
            new DetailedTranscription("world how",   [new("world", 0, 500), new(" how", 500, 1000)]));

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));    // 0..1s
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));    // 1..2s — full = 2s
        await sut.TranscribePendingAsync();                  // commits "hello", cursor = 16000
        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));    // 2..3s — full = 3s
        await sut.TranscribePendingAsync();

        Assert.Equal(3, fake.Calls.Count);
        // Third call sees the buffer minus the trim cursor: 96000 - 16000 = 80000 bytes (2.5s).
        Assert.Equal(3 * BytesPerSecond - BytesPerSecond / 2, fake.Calls[2].Audio.Length);
        // And the prompt is what we'd already committed.
        Assert.Equal("hello", fake.Calls[2].Prompt);
    }

    [Fact]
    public async Task Reset_ClearsTrimCursorAndAccumulator()
    {
        var fake = new DetailedSttFake(
            new DetailedTranscription("hello",       [new("hello", 0, 500)]),
            new DetailedTranscription("hello world", [new("hello", 0, 500), new(" world", 500, 1000)]));

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
        await sut.TranscribePendingAsync();
        Assert.True(sut.GetBytesCommittedThroughForTesting() > 0);
        Assert.NotEqual(string.Empty, sut.GetCommittedAccumulatorForTesting());

        sut.Reset();

        Assert.Equal(0, sut.GetBytesCommittedThroughForTesting());
        Assert.Equal(string.Empty, sut.GetCommittedAccumulatorForTesting());
    }

    [Fact]
    public async Task GetFinalResultAsync_TranscribesFullUntrimmedBuffer()
    {
        // After partial passes have advanced the cursor, the final pass must
        // see the FULL audio buffer — not the trimmed remainder. The agent's
        // dispatched transcription must always come from the highest-quality
        // single-shot pass over the complete utterance.
        var fake = new DetailedSttFake(
            // Pass 1 + 2: build up commits via LA-2, advancing the cursor.
            new DetailedTranscription("hello",        [new("hello", 0, 500)]),
            new DetailedTranscription("hello world",  [new("hello", 0, 500), new(" world", 500, 1000)]),
            // Final pass: this is the one we assert about.
            new DetailedTranscription("hello world final"));

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
        await sut.TranscribePendingAsync();
        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
        await sut.TranscribePendingAsync();
        Assert.True(sut.GetBytesCommittedThroughForTesting() > 0); // sanity: cursor moved

        var final = await sut.GetFinalResultAsync();

        // Final pass saw the FULL 2s buffer = 64000 bytes — NOT the slice after the cursor.
        Assert.Equal(2 * BytesPerSecond, fake.Calls[^1].Audio.Length);
        Assert.Equal("hello world final", final);
    }

    [Fact]
    public async Task WhisperEchoesPrompt_AccumulatorDoesNotDuplicateAlreadyCommittedWords()
    {
        // The real correctness risk of prompt echo: the same words land in
        // the committed accumulator twice. Without the guard, this scenario
        // would produce committedAccumulator = "hello hello world how"
        // because pass 4's LA-2 (vs pass 3's lastFullOutput="hello world how")
        // would commit the entire "hello world how" — but pass 2 already
        // committed "hello", so we'd append "hello world how" to the existing
        // "hello", giving "hello hello world how". The guard strips the
        // echoed prompt before LA-2 so each commit pass appends ONLY new words.
        var fake = new DetailedSttFake(
            new DetailedTranscription("hello",       [new("hello", 0, 500)]),
            new DetailedTranscription("hello world", [new("hello", 0, 500), new(" world", 500, 1000)]),
            new DetailedTranscription("hello world how",
                [new("hello", 0, 100), new(" world", 100, 600), new(" how", 600, 1100)]),
            new DetailedTranscription("hello world how are",
                [new("hello", 0, 100), new(" world", 100, 600), new(" how", 600, 1100), new(" are", 1100, 1600)]));

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        for (var i = 0; i < 4; i++)
        {
            sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));
            await sut.TranscribePendingAsync();
        }

        // No "hello hello" duplication.
        Assert.Equal("hello world how", sut.GetCommittedAccumulatorForTesting());
        Assert.DoesNotContain("hello hello", sut.GetCommittedAccumulatorForTesting());
    }

    [Fact]
    public void StripEchoedPrompt_ReturnsOriginal_WhenPromptIsNullOrEmpty()
    {
        Assert.Equal("hello", WhisperStreamingSpeechToText.StripEchoedPrompt("hello", null));
        Assert.Equal("hello", WhisperStreamingSpeechToText.StripEchoedPrompt("hello", string.Empty));
    }

    [Fact]
    public void StripEchoedPrompt_StripsLeadingPromptWords_CaseAndPunctuationInsensitive()
    {
        Assert.Equal("world how", WhisperStreamingSpeechToText.StripEchoedPrompt("hello world how", "hello"));
        Assert.Equal("world", WhisperStreamingSpeechToText.StripEchoedPrompt("Hello, world", "hello"));
        Assert.Equal("how are you", WhisperStreamingSpeechToText.StripEchoedPrompt("hello world how are you", "hello world"));
    }

    [Fact]
    public void StripEchoedPrompt_LeavesOutputUntouched_WhenPromptDoesNotMatchPrefix()
    {
        Assert.Equal("world how", WhisperStreamingSpeechToText.StripEchoedPrompt("world how", "hello"));
    }

    [Fact]
    public void StripEchoedTokens_HandlesSubWordTokensSpanningPromptWord()
    {
        // Whisper.cpp can split a single word into multiple sub-word BPE tokens.
        // For prompt "hello", tokens = [(" hel", 0, 200), ("lo", 200, 500), ("world", 500, 1000)]
        // The naive "drop N tokens for N prompt words" would drop only the first
        // token, leaving "lo" + "world" — wrong. The walk-by-words algorithm
        // accumulates token text until promptWordCount is reached, then drops
        // through that boundary.
        IReadOnlyList<TranscribedToken> tokens =
        [
            new(" hel",  0,   200),
            new("lo",    200, 500),
            new(" world", 500, 1000),
        ];

        var stripped = WhisperStreamingSpeechToText.StripEchoedTokens(tokens, "hello");

        Assert.Single(stripped);
        Assert.Equal(" world", stripped[0].Text);
        Assert.Equal(500, stripped[0].StartMs);
    }

    [Fact]
    public void ResolveCommittedAdvanceMs_SubtractsFirstTokenStart()
    {
        // After echo strip the first remaining token's StartMs is the new
        // origin. The cursor advance is duration through the committed-word
        // boundary, not absolute time from slice start.
        IReadOnlyList<TranscribedToken> tokens =
        [
            new(" world", 100, 600),
            new(" how",   600, 1100),
        ];

        var advance = WhisperStreamingSpeechToText.ResolveCommittedAdvanceMs("world", tokens);

        Assert.Equal(500, advance); // 600 - 100, not 600
    }

    [Fact]
    public async Task ResetDuringPass_DoesNotCorruptFreshState()
    {
        // Race scenario: a background pass is mid-flight when Reset() lands.
        // After Reset the wrapper must look like new — cursor=0, accumulator
        // empty, and no stale state from the in-flight pass.
        // We simulate the race by gating the fake's TranscribeDetailedAsync on
        // a TaskCompletionSource — Reset runs while the fake is "thinking",
        // then the fake completes and the wrapper attempts its write-back.
        var gate = new TaskCompletionSource<DetailedTranscription?>();
        var fake = new GatedDetailedSttFake(gate.Task);

        using var sut = new WhisperStreamingSpeechToText(fake, NullLogger<WhisperStreamingSpeechToText>.Instance);

        sut.AcceptAudio(CreateDummyPcm(BytesPerSecond));

        // Kick off the pass — it'll block in Whisper waiting for `gate`.
        var pending = sut.TranscribePendingAsync();

        // Race: Reset before the pass commits.
        sut.Reset();

        // Now let the in-flight pass complete with a stale result.
        gate.SetResult(new DetailedTranscription("stale hello", [new("stale", 0, 500), new(" hello", 500, 1000)]));
        await pending;

        // Post-Reset state must be unmodified by the stale pass.
        Assert.Equal(0, sut.GetBytesCommittedThroughForTesting());
        Assert.Equal(string.Empty, sut.GetCommittedAccumulatorForTesting());
        Assert.Equal(string.Empty, sut.GetPartialResult());
    }

    private static byte[] CreateDummyPcm(int bytes)
    {
        var data = new byte[bytes];
        Array.Fill(data, (byte)0x10);
        return data;
    }

    /// <summary>
    /// Test double: ISpeechToText that overrides TranscribeDetailedAsync directly
    /// with a scripted queue of DetailedTranscription results. Captures each call's
    /// audio and prompt arguments so tests can assert on the wrapper's behaviour.
    /// </summary>
    private sealed class DetailedSttFake : ISpeechToText
    {
        private readonly Queue<DetailedTranscription?> scripts;

        public DetailedSttFake(params DetailedTranscription?[] scripts)
        {
            this.scripts = new Queue<DetailedTranscription?>(scripts);
        }

        public bool IsReady => true;

        /// <summary>Audio (snapshot) and prompt forwarded by the streaming wrapper, in call order.</summary>
        public List<(byte[] Audio, string? Prompt)> Calls { get; } = [];

        public Task<string?> TranscribeAsync(byte[] pcmData, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "DetailedSttFake should not see TranscribeAsync — the streaming wrapper " +
                "is expected to use TranscribeDetailedAsync directly.");

        public Task<DetailedTranscription?> TranscribeDetailedAsync(
            byte[] pcmData, string? prompt, CancellationToken cancellationToken = default)
        {
            this.Calls.Add((pcmData, prompt));
            var next = this.scripts.Count > 0 ? this.scripts.Dequeue() : null;
            return Task.FromResult(next);
        }

        public void Dispose() { }
    }

    /// <summary>Test double whose TranscribeDetailedAsync blocks on a Task supplied by the test.</summary>
    private sealed class GatedDetailedSttFake(Task<DetailedTranscription?> gate) : ISpeechToText
    {
        public bool IsReady => true;

        public Task<string?> TranscribeAsync(byte[] pcmData, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("GatedDetailedSttFake should only see TranscribeDetailedAsync.");

        public Task<DetailedTranscription?> TranscribeDetailedAsync(
            byte[] pcmData, string? prompt, CancellationToken cancellationToken = default)
            => gate;

        public void Dispose() { }
    }
}
