using Cortex.Contained.Speech.Tts;

namespace Cortex.Contained.Speech.Tests.Tts;

public class ScriptDetectorTests
{
    [Fact]
    public void DetectDominantScript_Cyrillic_ReturnsCyrillic()
        => Assert.Equal(TextScript.Cyrillic, ScriptDetector.DetectDominantScript("Да, конечно могу!"));

    [Fact]
    public void DetectDominantScript_Latin_ReturnsLatin()
        => Assert.Equal(TextScript.Latin, ScriptDetector.DetectDominantScript("Anything else on your mind?"));

    [Fact]
    public void DetectDominantScript_ShortCyrillic_ReturnsCyrillic_LengthIndependent()
        => Assert.Equal(TextScript.Cyrillic, ScriptDetector.DetectDominantScript("Да"));

    [Fact]
    public void DetectDominantScript_MixedScript_DominantWins()
        => Assert.Equal(TextScript.Cyrillic, ScriptDetector.DetectDominantScript("Я люблю React и программирование"));

    [Fact]
    public void DetectDominantScript_DigitsAndPunctuationOnly_ReturnsNone()
        => Assert.Equal(TextScript.None, ScriptDetector.DetectDominantScript("123 — !?.,"));

    [Fact]
    public void DetectDominantScript_Empty_ReturnsNone()
        => Assert.Equal(TextScript.None, ScriptDetector.DetectDominantScript(""));

    [Theory]
    [InlineData("ru", TextScript.Cyrillic)]
    [InlineData("uk", TextScript.Cyrillic)]
    [InlineData("en", TextScript.Latin)]
    [InlineData("da", TextScript.Latin)]
    [InlineData("it", TextScript.Latin)]
    public void ScriptOf_KnownCodes_MapsToScript(string code, TextScript expected)
        => Assert.Equal(expected, ScriptDetector.ScriptOf(code));

    [Fact]
    public void DetectDominantScript_EqualLatinAndCyrillicCounts_TieBreakIsStable()
    {
        // Same letters, different order — result must not depend on input order.
        var a = ScriptDetector.DetectDominantScript("abвг");
        var b = ScriptDetector.DetectDominantScript("вгab");
        Assert.Equal(a, b);
    }

    [Fact]
    public void DetectDominantScript_JapaneseKanjiAndKana_ReturnsKana()
        => Assert.Equal(TextScript.Kana, ScriptDetector.DetectDominantScript("私は学生です"));

    [Theory]
    [InlineData("zh", TextScript.Han)]
    [InlineData("ja", TextScript.Kana)]
    [InlineData("ko", TextScript.Hangul)]
    [InlineData("xx", TextScript.Other)]
    public void ScriptOf_NonLatinAndUnknownCodes_MapCorrectly(string code, TextScript expected)
        => Assert.Equal(expected, ScriptDetector.ScriptOf(code));
}
