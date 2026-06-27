using Cortex.Contained.Channels.Discord;

namespace Cortex.Contained.Channels.Discord.Tests;

public class PlaybackTruncationTests
{
    [Fact]
    public void FullyPlayedOnly_JoinedWithEllipsis()
    {
        var p = new PlaybackProgress(["First sentence.", "Second one."], null, 0);
        Assert.Equal("First sentence. Second one. …", PlaybackTruncation.BuildPlayedText(p));
    }

    [Fact]
    public void InterruptedSentence_CutAtWordBoundary()
    {
        // ratio 0.5 of "the quick brown fox" (19 chars) -> round(9.5)=10 -> "the quick " -> snap to "the quick"
        var p = new PlaybackProgress(["Intro."], "the quick brown fox", 0.5);
        Assert.Equal("Intro. the quick …", PlaybackTruncation.BuildPlayedText(p));
    }

    [Fact]
    public void RatioClampedAboveOne()
    {
        var p = new PlaybackProgress([], "all of it", 1.4);
        Assert.Equal("all of it …", PlaybackTruncation.BuildPlayedText(p));
    }

    [Fact]
    public void NothingPlayed_JustEllipsis()
    {
        var p = new PlaybackProgress([], null, 0);
        Assert.Equal("…", PlaybackTruncation.BuildPlayedText(p));
    }

    [Fact]
    public void ZeroRatioInterrupted_DropsPartial()
    {
        var p = new PlaybackProgress(["Done sentence."], "not heard at all", 0.0);
        Assert.Equal("Done sentence. …", PlaybackTruncation.BuildPlayedText(p));
    }
}
