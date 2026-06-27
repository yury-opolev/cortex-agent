using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Speech.Tests.Tts;

public class LinguaLanguageDetectorTests
{
    [Fact]
    public void DetectTop_LongEnglish_ReturnsEn()
    {
        var d = new LinguaLanguageDetector(["en", "da", "ru"]);
        Assert.Equal("en", d.DetectTop("The harbor lights look calm this evening and the weather is mild."));
    }

    [Fact]
    public void DetectTop_LongDanish_ReturnsDa()
    {
        var d = new LinguaLanguageDetector(["en", "da", "ru"]);
        Assert.Equal("da", d.DetectTop("Vi mødes klokken syv på den lille café ved torvet i morgen aften."));
    }

    [Fact]
    public void DetectConfidences_SumsToApproxOne_AndContainsAllConfigured()
    {
        var d = new LinguaLanguageDetector(["en", "da", "ru"]);
        var c = d.DetectConfidences("This is a perfectly ordinary English sentence with several words in it.");
        Assert.True(c.ContainsKey("en"));
        Assert.True(c.ContainsKey("da"));
        Assert.True(c.ContainsKey("ru"));
        var sum = c.Values.Sum();
        Assert.InRange(sum, 0.95, 1.05);
        Assert.True(c["en"] > c["da"]);
    }

    [Fact]
    public void DetectTop_EmptyText_ReturnsFallback()
    {
        var d = new LinguaLanguageDetector(["en", "da", "ru"], fallback: "en");
        Assert.Equal("en", d.DetectTop(""));
    }

    [Fact]
    public void Fallback_DefaultsToFirstConfigured_WhenNotSpecified()
    {
        var d = new LinguaLanguageDetector(["en", "da", "ru"]);
        Assert.Equal("en", d.Fallback);
    }
}
