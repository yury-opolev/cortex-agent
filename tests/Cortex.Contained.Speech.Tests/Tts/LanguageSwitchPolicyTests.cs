using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Speech.Tests.Tts;

public class LanguageSwitchPolicyTests
{
    private static readonly LanguageSwitchThresholds T =
        new(MinSwitchChars: 60, SwitchConfidence: 0.80, SwitchMargin: 0.20);

    [Fact]
    public void Keep_WhenTextTooShort()
        => Assert.Equal("en", LanguageSwitchPolicy.Decide("en", candidate: "da", confTop: 0.99, confCurrent: 0.01, textLength: 30, T));

    [Fact]
    public void Keep_WhenTopConfidenceBelowBar()
        => Assert.Equal("en", LanguageSwitchPolicy.Decide("en", "da", 0.75, 0.10, 200, T));

    [Fact]
    public void Keep_WhenMarginTooSmall()
        => Assert.Equal("en", LanguageSwitchPolicy.Decide("en", "da", 0.85, 0.78, 200, T));

    [Fact]
    public void Switch_WhenAllThresholdsCleared()
        => Assert.Equal("da", LanguageSwitchPolicy.Decide("en", "da", 0.95, 0.04, 200, T));

    [Fact]
    public void Keep_WhenCandidateEqualsCurrent()
        => Assert.Equal("en", LanguageSwitchPolicy.Decide("en", "en", 0.99, 0.99, 9999, T));

    [Fact]
    public void DefaultThresholds_Are_60_080_020()
    {
        var d = LanguageSwitchThresholds.Default;
        Assert.Equal(60, d.MinSwitchChars);
        Assert.Equal(0.80, d.SwitchConfidence);
        Assert.Equal(0.20, d.SwitchMargin);
    }

    private static readonly LanguageSwitchThresholds Default = LanguageSwitchThresholds.Default;

    private static Dictionary<string, double> Conf(double en, double da, double ru) =>
        new() { ["en"] = en, ["da"] = da, ["ru"] = ru };

    [Fact]
    public void Resolve_CyrillicText_ReturnsRussian_EvenWhenShort()
        => Assert.Equal("ru", LanguageSwitchPolicy.Resolve(
            current: "en",
            text: "Интересно, а ты можешь распознавать русский текст?",
            confidences: Conf(0.0, 0.0, 1.0),
            Default));

    [Fact]
    public void Resolve_CyrillicText_IgnoresMisleadingConfidence()
        => Assert.Equal("ru", LanguageSwitchPolicy.Resolve(
            current: "en", text: "Да", confidences: Conf(0.9, 0.05, 0.05), Default));

    [Fact]
    public void Resolve_LatinText_LowConfidence_KeepsCurrent()
        => Assert.Equal("en", LanguageSwitchPolicy.Resolve(
            current: "en", text: "Okay then.", confidences: Conf(0.55, 0.45, 0.0), Default));

    [Fact]
    public void Resolve_LatinText_DecisiveDanish_SwitchesWithHysteresis()
        => Assert.Equal("da", LanguageSwitchPolicy.Resolve(
            current: "en",
            text: new string('a', 200),
            confidences: Conf(0.04, 0.95, 0.0),
            Default));

    [Fact]
    public void Resolve_NoLetters_KeepsCurrent()
        => Assert.Equal("ru", LanguageSwitchPolicy.Resolve(
            current: "ru", text: "123 — !?", confidences: Conf(0.5, 0.3, 0.2), Default));
}
