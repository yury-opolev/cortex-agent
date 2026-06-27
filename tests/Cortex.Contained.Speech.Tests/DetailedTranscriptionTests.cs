using Cortex.Contained.Speech;

namespace Cortex.Contained.Speech.Tests;

public class DetailedTranscriptionTests
{
    [Fact]
    public void Record_ExposesTextAndTokens()
    {
        var tokens = new List<TranscribedToken>
        {
            new("hello", 0, 500),
            new(" world", 500, 1000),
        };
        var result = new DetailedTranscription("hello world", tokens);

        Assert.Equal("hello world", result.Text);
        Assert.Equal(2, result.Tokens.Count);
        Assert.Equal(" world", result.Tokens[1].Text);
    }

    [Fact]
    public void Record_DefaultsTokensToEmpty_WhenOnlyTextProvided()
    {
        var result = new DetailedTranscription("hi");

        Assert.Equal("hi", result.Text);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void Record_TokensList_IsImmutable()
    {
        // The tokens list is exposed as IReadOnlyList<T>, so consumers can't
        // mutate it. (Structural equality across distinct lists is not
        // guaranteed and is not needed in production code — DetailedTranscription
        // is a one-shot result, never compared.)
        IReadOnlyList<TranscribedToken> tokens = [new("hi", 0, 200)];
        var result = new DetailedTranscription("hi", tokens);

        Assert.IsAssignableFrom<IReadOnlyList<TranscribedToken>>(result.Tokens);
        Assert.Single(result.Tokens);
    }
}
