using System.Runtime.CompilerServices;
using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Speech.Tests;

public class TtsProviderAdapterTests
{
    [Fact]
    public void CurrentVoice_ReturnsInitialVoice()
    {
        using var adapter = CreateAdapter("xenia");

        Assert.Equal("xenia", adapter.CurrentVoice);
    }

    [Fact]
    public void SetVoice_ValidVoice_ChangesCurrentVoice()
    {
        using var adapter = CreateAdapter("xenia");

        adapter.SetVoice("aidar");

        Assert.Equal("aidar", adapter.CurrentVoice);
    }

    [Fact]
    public void SetVoice_InvalidVoice_Throws()
    {
        using var adapter = CreateAdapter("xenia");

        Assert.Throws<ArgumentException>(() => adapter.SetVoice("nonexistent"));
    }

    [Fact]
    public void GetAvailableVoices_ReturnsProviderVoices()
    {
        using var adapter = CreateAdapter("xenia");

        var voices = adapter.GetAvailableVoices();

        Assert.Contains("xenia", voices);
        Assert.Contains("aidar", voices);
    }

    [Fact]
    public void OutputFormat_DelegatesToProvider()
    {
        using var adapter = CreateAdapter("xenia");

        Assert.Equal(48_000, adapter.OutputFormat.SampleRate);
    }

    [Fact]
    public void SupportsStreaming_DelegatesToProvider()
    {
        using var adapter = CreateAdapter("xenia");

        Assert.True(adapter.SupportsStreaming);
    }

    [Fact]
    public async Task SynthesizeAsync_DelegatesToProvider()
    {
        using var adapter = CreateAdapter("xenia");

        var pcm = await adapter.SynthesizeAsync("test");

        Assert.NotEmpty(pcm);
    }

    private static TtsProviderAdapter CreateAdapter(string voice) =>
        new(new StubProvider(), voice);

    private sealed class StubProvider : ITtsProvider
    {
        public string Name => "stub";

        public IReadOnlyList<TtsVoiceInfo> Voices =>
        [
            new TtsVoiceInfo("xenia", "ru", VoiceGender.Female),
            new TtsVoiceInfo("aidar", "ru", VoiceGender.Male),
        ];

        public bool IsReady => true;
        public string StatusDetail => "OK";
        public AudioFormat OutputFormat => AudioFormat.Silero;
        public bool SupportsStreaming => true;

        public Task<byte[]> SynthesizeAsync(string text, string voiceName, CancellationToken ct = default) =>
            Task.FromResult(new byte[100]);

        public async IAsyncEnumerable<byte[]> SynthesizeStreamingAsync(
            string text, string voiceName, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new byte[50];
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
