using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests.Tts;

public sealed class CompositeTtsEngineHintTests
{
    /// <summary>Minimal recording <see cref="ITtsProvider"/> double for routing tests.</summary>
    private sealed class RecordingTtsProvider : ITtsProvider
    {
        private readonly AudioFormat outputFormat;

        public RecordingTtsProvider(string name, IReadOnlyList<TtsVoiceInfo> voices, AudioFormat outputFormat)
        {
            this.Name = name;
            this.Voices = voices;
            this.outputFormat = outputFormat;
        }

        public string Name { get; }

        public IReadOnlyList<TtsVoiceInfo> Voices { get; }

        public bool IsReady => true;

        public string StatusDetail => "ready";

        public AudioFormat OutputFormat => this.outputFormat;

        public bool SupportsStreaming => true;

        public int SynthesizeCallCount { get; private set; }

        public Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken cancellationToken = default)
        {
            this.SynthesizeCallCount++;
            return Task.FromResult(new byte[] { 0x00, 0x01 });
        }

        public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
            string text,
            string voiceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.SynthesizeCallCount++;
            yield return new byte[] { 0x00, 0x01 };
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    private static RecordingTtsProvider MakeDanishProvider() => new(
        "roest-da",
        [new TtsVoiceInfo("mic", "da", VoiceGender.Female)],
        AudioFormat.Kokoro);

    private static RecordingTtsProvider MakeEnglishProvider() => new(
        "kokoro",
        [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)],
        AudioFormat.Kokoro);

    private static Dictionary<string, LanguageVoiceConfig> DaEnConfigs() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["da"] = new LanguageVoiceConfig { MaleVoice = "roest-da:mic", FemaleVoice = "roest-da:mic" },
            ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
        };

    private static RecordingTtsProvider MakeRussianProvider() => new(
        "silero-v5-russian",
        [new TtsVoiceInfo("kseniya", "ru", VoiceGender.Female)],
        AudioFormat.Silero);

    private static Dictionary<string, LanguageVoiceConfig> RuEnDaConfigs() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["da"] = new LanguageVoiceConfig { MaleVoice = "roest-da:mic", FemaleVoice = "roest-da:mic" },
            ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
            ["ru"] = new LanguageVoiceConfig { MaleVoice = "silero-v5-russian:kseniya", FemaleVoice = "silero-v5-russian:kseniya" },
        };

    /// <summary>
    /// Test A: When a language hint "da" is provided with English text, the engine must
    /// route to the Danish provider regardless of what Lingua would detect.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_LanguageHint_RoutesToHintedProvider_IgnoresLingua()
    {
        var daProvider = MakeDanishProvider();
        var enProvider = MakeEnglishProvider();

        using var engine = new CompositeTtsEngine(
            [daProvider, enProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: DaEnConfigs(),
            logger: NullLogger<CompositeTtsEngine>.Instance);

        // English text but we hint Danish — Lingua would detect English, hint must win.
        var pcm = await engine.SynthesizeAsync("Some English text here.", languageHint: "da");

        Assert.NotEmpty(pcm);
        Assert.Equal(1, daProvider.SynthesizeCallCount);
        Assert.Equal(0, enProvider.SynthesizeCallCount);
    }

    /// <summary>
    /// Test B: When languageHint is null the engine falls through to existing per-sentence
    /// Lingua detection and routes to the English provider for English text.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_NullHint_UsesLinguaDetection()
    {
        var daProvider = MakeDanishProvider();
        var enProvider = MakeEnglishProvider();

        using var engine = new CompositeTtsEngine(
            [daProvider, enProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: DaEnConfigs(),
            logger: NullLogger<CompositeTtsEngine>.Instance);

        // English text with no hint — Lingua routes to en.
        var pcm = await engine.SynthesizeAsync("Same English text.", languageHint: null);

        Assert.NotEmpty(pcm);
        Assert.Equal(1, enProvider.SynthesizeCallCount);
    }

    /// <summary>
    /// Test C: When a hint refers to an unconfigured language, the engine falls back
    /// to detection without throwing.
    /// </summary>
    [Fact]
    public async Task SynthesizeAsync_UnknownHint_FallsBackToDetection_DoesNotThrow()
    {
        var daProvider = MakeDanishProvider();
        var enProvider = MakeEnglishProvider();

        using var engine = new CompositeTtsEngine(
            [daProvider, enProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: DaEnConfigs(),
            logger: NullLogger<CompositeTtsEngine>.Instance);

        // "xx" is not a configured language — must fall back gracefully.
        var pcm = await engine.SynthesizeAsync("Text.", languageHint: "xx");

        Assert.NotEmpty(pcm);
        // The en provider must have been called (detection falls back to default).
        Assert.Equal(1, enProvider.SynthesizeCallCount);
        Assert.Equal(0, daProvider.SynthesizeCallCount);
    }

    /// <summary>
    /// Test A (streaming): When a language hint "da" is provided with English text, the
    /// streaming variant routes to the Danish provider.
    /// </summary>
    [Fact]
    public async Task SynthesizeStreamingAsync_LanguageHint_RoutesToHintedProvider()
    {
        var daProvider = MakeDanishProvider();
        var enProvider = MakeEnglishProvider();

        using var engine = new CompositeTtsEngine(
            [daProvider, enProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: DaEnConfigs(),
            logger: NullLogger<CompositeTtsEngine>.Instance);

        var chunks = new List<byte[]>();
        await foreach (var chunk in engine.SynthesizeStreamingAsync("Some English text here.", languageHint: "da"))
        {
            chunks.Add(chunk);
        }

        Assert.NotEmpty(chunks);
        Assert.Equal(1, daProvider.SynthesizeCallCount);
        Assert.Equal(0, enProvider.SynthesizeCallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_CyrillicSentence_OverridesStaleEnglishHint()
    {
        var da = MakeDanishProvider();
        var en = MakeEnglishProvider();
        var ru = MakeRussianProvider();

        using var engine = new CompositeTtsEngine(
            [da, en, ru],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: RuEnDaConfigs(),
            logger: NullLogger<CompositeTtsEngine>.Instance);

        // Stale sticky hint says "en", but the sentence is Cyrillic — script wins.
        var pcm = await engine.SynthesizeAsync("Да, конечно могу!", languageHint: "en");

        Assert.NotEmpty(pcm);
        Assert.Equal(1, ru.SynthesizeCallCount);
        Assert.Equal(0, en.SynthesizeCallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_BilingualReply_RoutesPerSentence_RuRuRuEn()
    {
        var da = MakeDanishProvider();
        var en = MakeEnglishProvider();
        var ru = MakeRussianProvider();

        using var engine = new CompositeTtsEngine(
            [da, en, ru],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: RuEnDaConfigs(),
            logger: NullLogger<CompositeTtsEngine>.Instance);

        var sentences = new[]
        {
            "Да, конечно могу!",
            "Понимаю и читаю русский без проблем.",
            "Но, как договорились, давай дальше по-английски.",
            "Anything else on your mind?",
        };
        foreach (var sentence in sentences)
        {
            await engine.SynthesizeAsync(sentence, languageHint: "en");
        }

        Assert.Equal(3, ru.SynthesizeCallCount);
        Assert.Equal(1, en.SynthesizeCallCount);
        Assert.Equal(0, da.SynthesizeCallCount);
    }

    [Fact]
    public async Task SynthesizeAsync_LatinSentence_StaleRussianHint_RoutesToEnglish()
    {
        var da = MakeDanishProvider();
        var en = MakeEnglishProvider();
        var ru = MakeRussianProvider();
        using var engine = new CompositeTtsEngine(
            [da, en, ru], defaultLanguage: "en", gender: VoiceGender.Female,
            languageConfigs: RuEnDaConfigs(), logger: NullLogger<CompositeTtsEngine>.Instance);

        // The incident: stale sticky hint "ru" on clearly-English text must NOT
        // route to the Cyrillic engine — detection wins, English speaks English.
        var pcm = await engine.SynthesizeAsync(
            "I can see your blinds are closed right now.", languageHint: "ru");

        Assert.NotEmpty(pcm);
        Assert.Equal(1, en.SynthesizeCallCount);
        Assert.Equal(0, ru.SynthesizeCallCount);
    }
}
