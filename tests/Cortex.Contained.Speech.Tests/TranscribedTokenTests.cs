using Cortex.Contained.Speech;

namespace Cortex.Contained.Speech.Tests;

public class TranscribedTokenTests
{
    [Fact]
    public void Record_ExposesTextStartAndEndTimestamps()
    {
        var token = new TranscribedToken("hello", 120, 480);

        Assert.Equal("hello", token.Text);
        Assert.Equal(120, token.StartMs);
        Assert.Equal(480, token.EndMs);
    }

    [Fact]
    public void Record_SupportsValueEquality()
    {
        var a = new TranscribedToken("hi", 0, 200);
        var b = new TranscribedToken("hi", 0, 200);
        var c = new TranscribedToken("hi", 0, 300);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }
}
