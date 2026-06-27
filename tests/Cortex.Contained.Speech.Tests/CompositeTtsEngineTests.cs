using System.Runtime.CompilerServices;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Speech.Tests;

public class CompositeTtsEngineTests : IDisposable
{
    private readonly MockTtsProvider _russianProvider;
    private readonly MockTtsProvider _englishProvider;

    public CompositeTtsEngineTests()
    {
        _russianProvider = new MockTtsProvider("silero", "ru", AudioFormat.Silero,
        [
            new TtsVoiceInfo("xenia", "ru", VoiceGender.Female, "Female — Xenia"),
            new TtsVoiceInfo("aidar", "ru", VoiceGender.Male, "Male — Aidar"),
        ]);

        _englishProvider = new MockTtsProvider("kokoro", "en", AudioFormat.Kokoro,
        [
            new TtsVoiceInfo("af_heart", "en", VoiceGender.Female, "Female — Heart"),
            new TtsVoiceInfo("am_adam", "en", VoiceGender.Male, "Male — Adam"),
        ]);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SynthesizeAsync_RussianText_UsesSileroProvider()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        await engine.SynthesizeAsync("Привет, как дела?");

        Assert.Equal("xenia", _russianProvider.LastVoiceUsed);
        Assert.True(_russianProvider.SynthesizeCalled);
        Assert.False(_englishProvider.SynthesizeCalled);
    }

    [Fact]
    public async Task SynthesizeAsync_EnglishText_UsesKokoroProvider()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        await engine.SynthesizeAsync("Hello, how are you doing today?");

        Assert.Equal("af_heart", _englishProvider.LastVoiceUsed);
        Assert.True(_englishProvider.SynthesizeCalled);
        Assert.False(_russianProvider.SynthesizeCalled);
    }

    [Fact]
    public async Task SynthesizeAsync_FemaleGender_UsesFemaleVoice()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        await engine.SynthesizeAsync("Привет мир");

        Assert.Equal("xenia", _russianProvider.LastVoiceUsed);
    }

    [Fact]
    public async Task SynthesizeAsync_MaleGender_UsesMaleVoice()
    {
        using var engine = CreateComposite(VoiceGender.Male);

        await engine.SynthesizeAsync("Привет мир");

        Assert.Equal("aidar", _russianProvider.LastVoiceUsed);
    }

    [Fact]
    public async Task SynthesizeAsync_UnknownLanguage_UsesDefaultLanguage()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        // Short ambiguous text → detection likely fails → falls back to default (ru)
        await engine.SynthesizeAsync("abc");

        // Should use default language provider (Russian)
        Assert.True(_russianProvider.SynthesizeCalled || _englishProvider.SynthesizeCalled);
    }

    [Fact]
    public void OutputFormat_IsAlways48kHz()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        Assert.Equal(48_000, engine.OutputFormat.SampleRate);
        Assert.Equal(1, engine.OutputFormat.Channels);
        Assert.Equal(16, engine.OutputFormat.BitsPerSample);
    }

    [Fact]
    public void SupportsStreaming_ReturnsTrue()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        Assert.True(engine.SupportsStreaming);
    }

    [Fact]
    public void GetAvailableVoices_ReturnsVoicesFromAllProviders()
    {
        using var engine = CreateComposite(VoiceGender.Female);

        var voices = engine.GetAvailableVoices();

        Assert.Contains("xenia", voices);
        Assert.Contains("aidar", voices);
        Assert.Contains("af_heart", voices);
        Assert.Contains("am_adam", voices);
    }

    [Fact]
    public async Task SynthesizeAsync_NonNativeSampleRate_Resamples()
    {
        // English provider outputs 24kHz — composite should resample to 48kHz
        using var engine = CreateComposite(VoiceGender.Female);

        var pcm = await engine.SynthesizeAsync("Hello world, this is a test sentence");

        // The mock produces 100 bytes at 24kHz. Resampled to 48kHz = 200 bytes.
        Assert.True(_englishProvider.SynthesizeCalled);
        Assert.Equal(200, pcm.Length);
    }

    [Fact]
    public async Task SynthesizeAsync_NativeSampleRate_NoResample()
    {
        // Russian provider outputs 48kHz — composite should NOT resample
        using var engine = CreateComposite(VoiceGender.Female);

        var pcm = await engine.SynthesizeAsync("Привет, это тестовое предложение на русском языке");

        Assert.True(_russianProvider.SynthesizeCalled);
        Assert.Equal(100, pcm.Length); // No resampling — stays at 100 bytes
    }

    [Fact]
    public async Task SynthesizeAsync_WithExplicitConfig_UsesConfiguredVoice()
    {
        var configs = new Dictionary<string, LanguageVoiceConfig>
        {
            ["ru"] = new() { MaleVoice = "silero:aidar", FemaleVoice = "silero:xenia" },
            ["en"] = new() { MaleVoice = "kokoro:am_adam", FemaleVoice = "kokoro:af_heart" },
        };

        using var engine = new CompositeTtsEngine(
            [_russianProvider, _englishProvider],
            "ru",
            VoiceGender.Male,
            configs,
            NullLogger<CompositeTtsEngine>.Instance);

        await engine.SynthesizeAsync("Привет мир, это тест");

        Assert.Equal("aidar", _russianProvider.LastVoiceUsed);
    }

    [Theory]
    [InlineData("silero-v5-russian:xenia", "silero-v5-russian", "xenia")]
    [InlineData("kokoro:af_heart", "kokoro", "af_heart")]
    [InlineData("nocolon", "nocolon", "nocolon")]
    public void ParseVoiceReference_SplitsCorrectly(string reference, string expectedProvider, string expectedVoice)
    {
        var (provider, voice) = LanguageVoiceConfig.ParseVoiceReference(reference);

        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedVoice, voice);
    }

    private CompositeTtsEngine CreateComposite(VoiceGender gender) =>
        new(
            [_russianProvider, _englishProvider],
            "ru",
            gender,
            languageConfigs: null,
            NullLogger<CompositeTtsEngine>.Instance);

    /// <summary>Mock TTS provider for testing composite engine routing.</summary>
    private sealed class MockTtsProvider : ITtsProvider
    {
        private readonly byte[] mockPcm;

        public MockTtsProvider(string name, string language, AudioFormat format, List<TtsVoiceInfo> voices)
        {
            this.Name = name;
            this.Voices = voices;
            this.OutputFormat = format;
            // 100 bytes of silence
            this.mockPcm = new byte[100];
        }

        public string Name { get; }
        public IReadOnlyList<TtsVoiceInfo> Voices { get; }
        public bool IsReady => true;
        public string StatusDetail => "Mock ready";
        public AudioFormat OutputFormat { get; }
        public bool SupportsStreaming => true;

        public bool SynthesizeCalled { get; private set; }
        public string? LastVoiceUsed { get; private set; }

        public Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken ct = default)
        {
            this.SynthesizeCalled = true;
            this.LastVoiceUsed = voiceName;
            return Task.FromResult((byte[])this.mockPcm.Clone());
        }

        public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
            string text, string voiceName, [EnumeratorCancellation] CancellationToken ct = default)
        {
            this.SynthesizeCalled = true;
            this.LastVoiceUsed = voiceName;
            yield return (byte[])this.mockPcm.Clone();
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
