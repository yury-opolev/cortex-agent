using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class TextFolderTests
{
    [Theory]
    [InlineData("Søren", "soren")]
    [InlineData("ÆBLE", "aeble")]
    [InlineData("Århus", "arhus")]
    [InlineData("Straße", "strasse")]
    [InlineData("Ёлка", "елка")]
    [InlineData("café naïve", "cafe naive")]
    [InlineData("ﬁle", "file")]
    public void Fold_KnownCharacters_AreFolded(string input, string expected)
    {
        Assert.Equal(expected, TextFolder.Fold(input));
    }

    [Fact]
    public void Fold_ShortI_IsKept()
    {
        Assert.Equal("йогурт мой", TextFolder.Fold("Йогурт мой"));
    }

    [Fact]
    public void Fold_LoneSurrogate_IsReplacedNotThrown()
    {
        Assert.Equal("a\uFFFDb", TextFolder.Fold("a\uD800b"));
    }

    [Fact]
    public void Fold_Null_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TextFolder.Fold(null));
    }
}

public class HistoryTokenizerTests
{
    [Fact]
    public void Tokenize_PunctuationAndUnderscore_AreSeparators()
    {
        Assert.Equal(["memory", "search", "e", "mail", "42"], HistoryTokenizer.Tokenize("memory_search, e-mail 42!"));
    }

    [Fact]
    public void CountTokens_MatchesTokenize()
    {
        const string text = "привет мир — hello [attachment:image/png]";

        Assert.Equal(HistoryTokenizer.Tokenize(text).Count, HistoryTokenizer.CountTokens(text));
        Assert.Equal(6, HistoryTokenizer.CountTokens(text));
    }
}
