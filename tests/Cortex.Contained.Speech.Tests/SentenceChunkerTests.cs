using Cortex.Contained.Speech;
using Xunit;

namespace Cortex.Contained.Speech.Tests;

public class SentenceChunkerTests
{
    [Fact]
    public void Split_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(SentenceChunker.Split(""));
        Assert.Empty(SentenceChunker.Split("   "));
    }

    [Fact]
    public void Split_SingleShortSentence_ReturnsOneChunk()
    {
        var result = SentenceChunker.Split("Hello world.");
        Assert.Equal(["Hello world."], result);
    }

    [Fact]
    public void Split_MultipleSentences_SplitsAtBoundaries()
    {
        var result = SentenceChunker.Split("First sentence. Second sentence! Third sentence?");
        Assert.Equal(
            ["First sentence.", "Second sentence!", "Third sentence?"],
            result);
    }

    [Fact]
    public void Split_AbbreviationsNotTreatedAsSentenceEnd()
    {
        var result = SentenceChunker.Split("Mr. Smith went to Washington. Then he came back.");
        Assert.Equal(
            ["Mr. Smith went to Washington.", "Then he came back."],
            result);
    }

    [Theory]
    [InlineData("e.g.")]
    [InlineData("i.e.")]
    [InlineData("etc.")]
    [InlineData("Dr.")]
    [InlineData("U.S.")]
    public void Split_CommonAbbreviations_DoesNotSplit(string abbrev)
    {
        var input = $"He used {abbrev} for emphasis. Then explained.";
        var result = SentenceChunker.Split(input);
        Assert.Equal(2, result.Count);
        Assert.Contains(abbrev, result[0]);
    }

    [Fact]
    public void Split_NoTerminalPunctuation_AppendsPeriod()
    {
        var result = SentenceChunker.Split("No period here");
        Assert.Single(result);
        Assert.EndsWith(".", result[0]);
    }

    [Fact]
    public void Split_SentenceOverMaxChunkChars_ClauseSplitsAtCommas()
    {
        var longSentence = "This is a very long sentence, which contains many words, " +
                           "and it should be split into smaller chunks, because it exceeds " +
                           "the maximum chunk length of fifty characters.";

        var result = SentenceChunker.Split(longSentence, maxChunkChars: 50);

        Assert.True(result.Count > 1);
        // Non-final fragments end with comma (continuing prosody)
        for (var i = 0; i < result.Count - 1; i++)
        {
            Assert.EndsWith(",", result[i]);
        }
        // Final fragment keeps the original terminator
        Assert.EndsWith(".", result[^1]);
    }

    [Fact]
    public void Split_EllipsisHandledCorrectly()
    {
        var result = SentenceChunker.Split("Wait... what was that? It happened again.");
        // Ellipsis should not split mid-thought
        Assert.Equal(2, result.Count);
        Assert.Contains("Wait", result[0]);
        Assert.Equal("It happened again.", result[1]);
    }

    [Fact]
    public void Split_MultipleWhitespace_Normalized()
    {
        var result = SentenceChunker.Split("Word   word.  Another   sentence.");
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain("  ", result[0]);
    }

    [Fact]
    public void Split_DefaultMaxChunkChars_Is600()
    {
        // A 200-char sentence is well under 600 → emitted as a single chunk.
        var sentence = string.Join(" ", Enumerable.Repeat("word", 40)) + ".";
        Assert.True(sentence.Length < 600);
        var result = SentenceChunker.Split(sentence);
        Assert.Single(result);
    }
}
