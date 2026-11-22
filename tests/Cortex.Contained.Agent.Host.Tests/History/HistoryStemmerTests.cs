using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryStemmerTests
{
    [Theory]
    [InlineData("поездом", "поезд")]
    [InlineData("поезда", "поезд")]
    [InlineData("ёлки", "елк")]
    public void Stems_Cyrillic_UsesRussian(string token, string stem)
    {
        Assert.Equal([stem], HistoryStemmer.Stems(TextFolder.Fold(token)));
    }

    [Fact]
    public void Stems_Latin_KeepsDanishAndEnglishStems()
    {
        Assert.Equal(["rejs", "rejser"], HistoryStemmer.Stems("rejser"));
        Assert.Equal(["train"], HistoryStemmer.Stems("trains"));
        Assert.Equal(["oslo"], HistoryStemmer.Stems("oslo"));
    }

    [Fact]
    public void Stems_DigitsAndOtherScripts_Unchanged()
    {
        Assert.Equal(["2041"], HistoryStemmer.Stems("2041"));
        Assert.Equal(["東京"], HistoryStemmer.Stems("東京"));
    }

    [Fact]
    public void StemBody_StemsEveryTokenOfFoldedText()
    {
        var body = HistoryStemmer.StemBody(TextFolder.Fold("Поездом в Oslo, trains 12"));

        Assert.Equal("поезд в oslo train 12", body);
    }
}
