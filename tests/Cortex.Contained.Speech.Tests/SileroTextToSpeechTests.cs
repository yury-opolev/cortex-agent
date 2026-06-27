using Cortex.Contained.Speech;
using Cortex.Contained.Speech.Tts;
using SileroSharp;

namespace Cortex.Contained.Speech.Tests;

public class SileroTextToSpeechTests
{
    // ── Voice resolution (all variants) ──────────────────────────────────

    [Fact]
    public void GetAllKnownVoiceNames_ReturnsAllVoices()
    {
        var voices = SileroTextToSpeech.GetAllKnownVoiceNames();

        Assert.Contains("xenia", voices);
        Assert.Contains("aidar", voices);
        Assert.Contains("baya", voices);
        Assert.Contains("kseniya", voices);
        Assert.Contains("ru_dmitriy", voices);
        Assert.Contains("ru_ekaterina", voices);
        Assert.True(voices.Count >= 32, $"Expected at least 32 voices, got {voices.Count}");
    }

    [Fact]
    public void ResolveVoice_ValidName_ReturnsVoice()
    {
        var voice = SileroTextToSpeech.ResolveVoice("xenia");

        Assert.Equal("xenia", voice.Name);
        Assert.Equal(3, voice.SpeakerId);
    }

    [Fact]
    public void ResolveVoice_CaseInsensitive_ReturnsVoice()
    {
        var voice = SileroTextToSpeech.ResolveVoice("XENIA");

        Assert.Equal("xenia", voice.Name);
    }

    [Fact]
    public void ResolveVoice_InvalidName_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => SileroTextToSpeech.ResolveVoice("nonexistent_voice"));

        Assert.Contains("Unknown Silero voice", ex.Message);
    }

    [Fact]
    public void GetDefaultModelDir_ReturnsExpectedPath()
    {
        var dir = SileroTextToSpeech.GetDefaultModelDir();

        Assert.Contains("Cortex", dir);
        Assert.Contains("models", dir);
        Assert.Contains("silero", dir);
    }

    // ── Variant filtering ────────────────────────────────────────────────

    [Fact]
    public void GetVoiceNamesForVariant_V5Russian_Returns4Voices()
    {
        var voices = SileroTextToSpeech.GetVoiceNamesForVariant(SileroModelVariant.V5Russian);

        Assert.Equal(4, voices.Count);
        Assert.Contains("xenia", voices);
        Assert.Contains("aidar", voices);
        Assert.Contains("baya", voices);
        Assert.Contains("kseniya", voices);
    }

    [Fact]
    public void GetVoiceNamesForVariant_V5CisBase_ReturnsRuPrefixedVoices()
    {
        var voices = SileroTextToSpeech.GetVoiceNamesForVariant(SileroModelVariant.V5CisBase);

        Assert.True(voices.Count >= 28, $"Expected at least 28 CIS voices, got {voices.Count}");
        Assert.All(voices, v => Assert.StartsWith("ru_", v));
    }

    [Fact]
    public void GetVoiceNamesForVariant_V5Russian_ExcludesRuPrefixed()
    {
        var voices = SileroTextToSpeech.GetVoiceNamesForVariant(SileroModelVariant.V5Russian);

        Assert.DoesNotContain("ru_dmitriy", voices);
        Assert.DoesNotContain("ru_ekaterina", voices);
    }

    [Fact]
    public void GetVoiceNamesForVariant_V5CisBase_ExcludesNonPrefixed()
    {
        var voices = SileroTextToSpeech.GetVoiceNamesForVariant(SileroModelVariant.V5CisBase);

        Assert.DoesNotContain("xenia", voices);
        Assert.DoesNotContain("aidar", voices);
    }

    // ── Variant parsing ──────────────────────────────────────────────────

    [Theory]
    [InlineData("v5-russian", SileroModelVariant.V5Russian)]
    [InlineData("v5-cis-base", SileroModelVariant.V5CisBase)]
    [InlineData("V5-CIS-BASE", SileroModelVariant.V5CisBase)]
    [InlineData("cis", SileroModelVariant.V5CisBase)]
    [InlineData("mit", SileroModelVariant.V5CisBase)]
    [InlineData(null, SileroModelVariant.V5Russian)]
    [InlineData("", SileroModelVariant.V5Russian)]
    [InlineData("unknown", SileroModelVariant.V5Russian)]
    public void ParseVariant_MapsCorrectly(string? input, SileroModelVariant expected)
    {
        Assert.Equal(expected, SileroTextToSpeech.ParseVariant(input));
    }

    // ── Audio format ─────────────────────────────────────────────────────

    [Fact]
    public void OutputFormat_Is48kHzMono16Bit()
    {
        Assert.Equal(48_000, AudioFormat.Silero.SampleRate);
        Assert.Equal(1, AudioFormat.Silero.Channels);
        Assert.Equal(16, AudioFormat.Silero.BitsPerSample);
    }
}
