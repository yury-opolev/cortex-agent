using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests.Tts;

public sealed class CompositeTtsEngineTests
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

        public bool Ready { get; set; } = true;

        public bool IsReady => this.Ready;

        public string StatusDetail => "ready";

        public AudioFormat OutputFormat => this.outputFormat;

        public bool SupportsStreaming => true;

        public string? LastVoiceName { get; private set; }

        public int SynthesizeCallCount { get; private set; }

        public Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken cancellationToken = default)
        {
            this.LastVoiceName = voiceName;
            this.SynthesizeCallCount++;

            // Return a small non-empty PCM buffer (one 16-bit sample) so the
            // engine's resample path has something to work with.
            return Task.FromResult(new byte[] { 0x00, 0x01 });
        }

        public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
            string text,
            string voiceName,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastVoiceName = voiceName;
            this.SynthesizeCallCount++;
            yield return new byte[] { 0x00, 0x01 };
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task SynthesizeAsync_DanishText_RoutesToDanishProvider()
    {
        var danishProvider = new RecordingTtsProvider(
            "roest-da",
            [new TtsVoiceInfo("mic", "da", VoiceGender.Female)],
            AudioFormat.Kokoro);

        var englishProvider = new RecordingTtsProvider(
            "kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)],
            AudioFormat.Kokoro);

        var languageConfigs = new Dictionary<string, LanguageVoiceConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["da"] = new LanguageVoiceConfig { MaleVoice = "roest-da:nic", FemaleVoice = "roest-da:mic" },
            ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
        };

        using var engine = new CompositeTtsEngine(
            [danishProvider, englishProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: languageConfigs,
            logger: NullLogger<CompositeTtsEngine>.Instance);

        var pcm = await engine.SynthesizeAsync("Hej, hvordan går det med dig i dag?");

        Assert.NotEmpty(pcm);
        Assert.Equal(1, danishProvider.SynthesizeCallCount);
        Assert.Equal(0, englishProvider.SynthesizeCallCount);
        Assert.Equal("mic", danishProvider.LastVoiceName);
    }

    [Fact]
    public async Task DanishText_ProviderNotReady_FallsBackToDefaultVoice()
    {
        var danishProvider = new RecordingTtsProvider(
            "roest-da",
            [new TtsVoiceInfo("mic", "da", VoiceGender.Female)],
            AudioFormat.Kokoro)
        {
            Ready = false,
        };

        var englishProvider = new RecordingTtsProvider(
            "kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)],
            AudioFormat.Kokoro);

        var languageConfigs = new Dictionary<string, LanguageVoiceConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["da"] = new LanguageVoiceConfig { MaleVoice = "roest-da:nic", FemaleVoice = "roest-da:mic" },
            ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
        };

        using var engine = new CompositeTtsEngine(
            [danishProvider, englishProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: languageConfigs,
            logger: NullLogger<CompositeTtsEngine>.Instance);

        var pcm = await engine.SynthesizeAsync("Hej, hvordan går det med dig i dag?");

        Assert.NotEmpty(pcm);
        Assert.Equal(0, danishProvider.SynthesizeCallCount);
        Assert.Equal(1, englishProvider.SynthesizeCallCount);
        Assert.Equal("af_heart", englishProvider.LastVoiceName);
    }

    [Fact]
    public async Task DanishText_ProviderBecomesReady_RoutesToDanish()
    {
        var danishProvider = new RecordingTtsProvider(
            "roest-da",
            [new TtsVoiceInfo("mic", "da", VoiceGender.Female)],
            AudioFormat.Kokoro)
        {
            Ready = false,
        };

        var englishProvider = new RecordingTtsProvider(
            "kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)],
            AudioFormat.Kokoro);

        var languageConfigs = new Dictionary<string, LanguageVoiceConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["da"] = new LanguageVoiceConfig { MaleVoice = "roest-da:nic", FemaleVoice = "roest-da:mic" },
            ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
        };

        using var engine = new CompositeTtsEngine(
            [danishProvider, englishProvider],
            defaultLanguage: "en",
            gender: VoiceGender.Female,
            languageConfigs: languageConfigs,
            logger: NullLogger<CompositeTtsEngine>.Instance);

        const string danishText = "Hej, hvordan går det med dig i dag?";

        // First call: Danish provider not ready → falls back to default voice.
        await engine.SynthesizeAsync(danishText);
        Assert.Equal(0, danishProvider.SynthesizeCallCount);
        Assert.Equal(1, englishProvider.SynthesizeCallCount);

        // Container warms up — provider becomes ready WITHOUT rebuilding the engine.
        danishProvider.Ready = true;

        // Next call must route to Danish, proving readiness is read live.
        await engine.SynthesizeAsync(danishText);
        Assert.Equal(1, danishProvider.SynthesizeCallCount);
        Assert.Equal("mic", danishProvider.LastVoiceName);
        Assert.Equal(1, englishProvider.SynthesizeCallCount);
    }

    private static RecordingTtsProvider RoestWithBothGenders() => new(
        "roest-da",
        [new TtsVoiceInfo("mic", "da", VoiceGender.Female), new TtsVoiceInfo("nic", "da", VoiceGender.Male)],
        AudioFormat.Kokoro);

    private static Dictionary<string, LanguageVoiceConfig> DaEnConfigs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["da"] = new LanguageVoiceConfig { MaleVoice = "roest-da:nic", FemaleVoice = "roest-da:mic" },
        ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:am_adam", FemaleVoice = "kokoro:af_heart" },
    };

    [Fact]
    public async Task FemaleGender_PicksConfiguredFemaleVoice()
    {
        var roest = RoestWithBothGenders();
        var kokoro = new RecordingTtsProvider(
            "kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female), new TtsVoiceInfo("am_adam", "en", VoiceGender.Male)],
            AudioFormat.Kokoro);

        using var engine = new CompositeTtsEngine(
            [roest, kokoro], "en", VoiceGender.Female, DaEnConfigs(), NullLogger<CompositeTtsEngine>.Instance);

        await engine.SynthesizeAsync("Hej, hvordan går det med dig i dag?");

        Assert.Equal("mic", roest.LastVoiceName); // female slot, not nic
    }

    [Fact]
    public async Task MaleGender_PicksConfiguredMaleVoice()
    {
        var roest = RoestWithBothGenders();
        var kokoro = new RecordingTtsProvider(
            "kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female), new TtsVoiceInfo("am_adam", "en", VoiceGender.Male)],
            AudioFormat.Kokoro);

        using var engine = new CompositeTtsEngine(
            [roest, kokoro], "en", VoiceGender.Male, DaEnConfigs(), NullLogger<CompositeTtsEngine>.Instance);

        await engine.SynthesizeAsync("Hej, hvordan går det med dig i dag?");

        Assert.Equal("nic", roest.LastVoiceName); // male slot
    }

    [Fact]
    public async Task ConfiguredLanguage_UnknownProvider_NotRoutable_FallsBackToDefault()
    {
        // da is configured but points at a provider that isn't registered →
        // it must NOT be in the routing map, so Danish text falls back to en.
        var kokoro = new RecordingTtsProvider(
            "kokoro",
            [new TtsVoiceInfo("af_heart", "en", VoiceGender.Female)],
            AudioFormat.Kokoro);

        var configs = new Dictionary<string, LanguageVoiceConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["da"] = new LanguageVoiceConfig { MaleVoice = "ghost:x", FemaleVoice = "ghost:x" }, // 'ghost' not registered
            ["en"] = new LanguageVoiceConfig { MaleVoice = "kokoro:af_heart", FemaleVoice = "kokoro:af_heart" },
        };

        using var engine = new CompositeTtsEngine(
            [kokoro], "en", VoiceGender.Female, configs, NullLogger<CompositeTtsEngine>.Instance);

        await engine.SynthesizeAsync("Hej, hvordan går det med dig i dag?");

        Assert.Equal(1, kokoro.SynthesizeCallCount);
        Assert.Equal("af_heart", kokoro.LastVoiceName);
    }
}
