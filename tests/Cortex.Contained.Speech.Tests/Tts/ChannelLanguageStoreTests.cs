using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Speech.Tests.Tts;

public class ChannelLanguageStoreTests
{
    private sealed class FakeDetector(string top, IReadOnlyDictionary<string, double> conf) : ILanguageDetector
    {
        public string Fallback => "en";
        public string DetectTop(string t) => top;
        public IReadOnlyDictionary<string, double> DetectConfidences(string t) => conf;
    }

    [Fact]
    public void NewChannel_ReturnsDefaultLanguage()
    {
        var s = new ChannelLanguageStore(defaultLanguage: "en");
        Assert.Equal("en", s.GetCurrent("ch1"));
    }

    [Fact]
    public void Update_BelowSwitchBar_KeepsCurrent()
    {
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("da", new Dictionary<string, double> { ["da"] = 0.99, ["en"] = 0.01 });
        var r = s.UpdateFromDetection("ch1", text: "short", detector: fake, thresholds: LanguageSwitchThresholds.Default);

        Assert.Equal("en", r.NewCurrent);
        Assert.False(r.Switched);
        Assert.Equal("en", r.CurrentBefore);
        Assert.Equal("en", r.CurrentAfter);
        Assert.Equal("da", r.Candidate);
        Assert.Equal(5, r.TextLength);
        Assert.Equal("en", s.GetCurrent("ch1"));
    }

    [Fact]
    public void Update_AboveSwitchBar_FlipsCurrent()
    {
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("da", new Dictionary<string, double> { ["da"] = 0.95, ["en"] = 0.04 });
        var text = new string('a', 200);
        var r = s.UpdateFromDetection("ch1", text, fake, LanguageSwitchThresholds.Default);

        Assert.Equal("da", r.NewCurrent);
        Assert.True(r.Switched);
        Assert.Equal("en", r.CurrentBefore);
        Assert.Equal("da", r.CurrentAfter);
        Assert.Equal(0.95, r.ConfTop);
        Assert.Equal(0.04, r.ConfCurrentBefore);
        Assert.Equal("da", s.GetCurrent("ch1"));
    }

    [Fact]
    public void PerChannelIsolation()
    {
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("da", new Dictionary<string, double> { ["da"] = 0.95, ["en"] = 0.04 });
        var text = new string('a', 200);

        s.UpdateFromDetection("ch1", text, fake, LanguageSwitchThresholds.Default);

        Assert.Equal("da", s.GetCurrent("ch1"));
        Assert.Equal("en", s.GetCurrent("ch2"));
    }

    [Fact]
    public void EmptyText_NoSwitch_ReportsResultWithoutCrashing()
    {
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("da", new Dictionary<string, double> { ["da"] = 0.99, ["en"] = 0.01 });

        var r = s.UpdateFromDetection("ch1", text: "", detector: fake, thresholds: LanguageSwitchThresholds.Default);

        Assert.False(r.Switched);
        Assert.Equal("en", r.CurrentAfter);
        Assert.Equal(0, r.TextLength);
    }

    [Fact]
    public void CurrentConfidence_MissingFromDetectorOutput_TreatedAsZero()
    {
        // Detector returns only the top language; current's confidence is unknown -> 0.
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("da", new Dictionary<string, double> { ["da"] = 0.95 });
        var text = new string('a', 200);

        var r = s.UpdateFromDetection("ch1", text, fake, LanguageSwitchThresholds.Default);

        Assert.True(r.Switched);
        Assert.Equal(0d, r.ConfCurrentBefore);
    }

    [Fact]
    public void Update_ShortCyrillicQuestion_SwitchesToRussian()
    {
        // Reproduces the real bug: a 50-char Russian question that the old
        // 60-char length gate wrongly kept as English.
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("ru", new Dictionary<string, double> { ["en"] = 0.0, ["da"] = 0.0, ["ru"] = 1.0 });
        var text = "Интересно, а ты можешь распознавать русский текст?";

        var r = s.UpdateFromDetection("ch1", text, fake, LanguageSwitchThresholds.Default);

        Assert.True(r.Switched);
        Assert.Equal("ru", r.CurrentAfter);
        Assert.Equal("ru", s.GetCurrent("ch1"));
    }

    [Fact]
    public void Update_EnglishReply_DoesNotDriftToDanish()
    {
        // English text with only weak same-script signal must stay English.
        var s = new ChannelLanguageStore("en");
        var fake = new FakeDetector("da", new Dictionary<string, double> { ["en"] = 0.52, ["da"] = 0.48, ["ru"] = 0.0 });
        var text = "So I was thinking we could go for a walk later.";

        var r = s.UpdateFromDetection("ch1", text, fake, LanguageSwitchThresholds.Default);

        Assert.False(r.Switched);
        Assert.Equal("en", s.GetCurrent("ch1"));
    }
}
