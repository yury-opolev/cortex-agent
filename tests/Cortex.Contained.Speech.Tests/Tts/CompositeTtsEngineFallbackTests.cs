using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests.Tts;

public sealed class CompositeTtsEngineFallbackTests
{
    private sealed class FakeProvider(string name, IReadOnlyList<TtsVoiceInfo> voices, bool emitAudio) : ITtsProvider
    {
        public string Name => name;
        public IReadOnlyList<TtsVoiceInfo> Voices => voices;
        public bool IsReady => true;
        public string StatusDetail => "ready";
        public AudioFormat OutputFormat => AudioFormat.Silero;
        public bool SupportsStreaming => true;
        public int Calls { get; private set; }
        public Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken ct = default)
        {
            this.Calls++;
            return Task.FromResult(emitAudio ? new byte[] { 1, 2, 3, 4 } : Array.Empty<byte>());
        }
        public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(string text, string voiceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            this.Calls++;
            if (emitAudio) { yield return new byte[] { 1, 2, 3, 4 }; }
            await Task.CompletedTask.ConfigureAwait(false);
        }
        public void Dispose() { }
    }

    private static Dictionary<string, LanguageVoiceConfig> RuEnConfigs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
        ["ru"] = new LanguageVoiceConfig { MaleVoice = "silero:kseniya", FemaleVoice = "silero:kseniya" },
    };

    [Fact]
    public async Task SynthesizeAsync_ResolvedEngineYieldsNothing_FallsBackToDefaultVoice()
    {
        var ru = new FakeProvider("silero", [new TtsVoiceInfo("kseniya", "ru", VoiceGender.Female)], emitAudio: false);
        var en = new FakeProvider("kokoro", [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)], emitAudio: true);
        using var engine = new CompositeTtsEngine(
            [en, ru], defaultLanguage: "en", gender: VoiceGender.Female,
            languageConfigs: RuEnConfigs(), logger: NullLogger<CompositeTtsEngine>.Instance);

        // Cyrillic text resolves to the (failing) ru engine; must fall back to en default.
        var pcm = await engine.SynthesizeAsync("Привет, как дела?", languageHint: null);

        Assert.NotEmpty(pcm);
        Assert.Equal(1, ru.Calls);
        Assert.True(en.Calls >= 1);
    }

    [Fact]
    public async Task SynthesizeStreamingAsync_ResolvedEngineYieldsNothing_FallsBackToDefaultVoice()
    {
        var ru = new FakeProvider("silero", [new TtsVoiceInfo("kseniya", "ru", VoiceGender.Female)], emitAudio: false);
        var en = new FakeProvider("kokoro", [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)], emitAudio: true);
        using var engine = new CompositeTtsEngine(
            [en, ru], defaultLanguage: "en", gender: VoiceGender.Female,
            languageConfigs: RuEnConfigs(), logger: NullLogger<CompositeTtsEngine>.Instance);

        var chunks = new List<byte[]>();
        await foreach (var c in engine.SynthesizeStreamingAsync("Привет, как дела?", languageHint: null))
        {
            chunks.Add(c);
        }

        Assert.NotEmpty(chunks);
        Assert.True(en.Calls >= 1);
    }

    [Fact]
    public async Task SynthesizeAsync_DefaultEngineUsed_NoDoubleSynthesis()
    {
        // When the resolved engine IS the default and it works, there must be no second call.
        var en = new FakeProvider("kokoro", [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)], emitAudio: true);
        using var engine = new CompositeTtsEngine(
            [en], defaultLanguage: "en", gender: VoiceGender.Female,
            languageConfigs: new(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
            },
            logger: NullLogger<CompositeTtsEngine>.Instance);

        var pcm = await engine.SynthesizeAsync("Hello there friend.", languageHint: null);

        Assert.NotEmpty(pcm);
        Assert.Equal(1, en.Calls);
    }

    [Fact]
    public async Task SynthesizeStreamingAsync_HealthyStream_YieldsFirstChunkBeforeSecondIsProduced()
    {
        var gate = new TaskCompletionSource();
        var en = new GatedProvider("kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)], gate);
        using var engine = new CompositeTtsEngine(
            [en], defaultLanguage: "en", gender: VoiceGender.Female,
            languageConfigs: new(StringComparer.OrdinalIgnoreCase)
            {
                ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
            },
            logger: NullLogger<CompositeTtsEngine>.Instance);

        await using var e = engine.SynthesizeStreamingAsync("Hello there.", languageHint: null)
            .GetAsyncEnumerator();

        // First chunk must arrive WITHOUT the second chunk having been produced
        // (i.e. without the gate being released) — proves incremental streaming.
        var first = await e.MoveNextAsync();
        Assert.True(first);
        Assert.NotEmpty(e.Current);
        Assert.Equal(1, en.ChunksProduced); // only the first chunk produced so far

        gate.SetResult();                    // now allow the second chunk
        var second = await e.MoveNextAsync();
        Assert.True(second);
        Assert.Equal(2, en.ChunksProduced);
    }

    private sealed class GatedProvider(string name, IReadOnlyList<TtsVoiceInfo> voices, TaskCompletionSource gate) : ITtsProvider
    {
        public string Name => name;
        public IReadOnlyList<TtsVoiceInfo> Voices => voices;
        public bool IsReady => true;
        public string StatusDetail => "ready";
        public AudioFormat OutputFormat => AudioFormat.Silero;
        public bool SupportsStreaming => true;
        public int ChunksProduced { get; private set; }
        public async Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken ct = default)
        {
            var buffer = new MemoryStream();
            await foreach (var c in this.SynthesizeStreamingAsync(text, voiceName, ct).ConfigureAwait(false)) { buffer.Write(c); }
            return buffer.ToArray();
        }
        public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(string text, string voiceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            this.ChunksProduced++;
            yield return new byte[] { 1, 2 };
            await gate.Task.ConfigureAwait(false);
            this.ChunksProduced++;
            yield return new byte[] { 3, 4 };
        }
        public void Dispose() { }
    }
}
