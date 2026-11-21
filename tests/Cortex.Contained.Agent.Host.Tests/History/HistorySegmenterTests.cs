using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistorySegmenterTests
{
    private static readonly SegmenterSettings Settings = SegmenterSettings.From(new HistoryOptions { MaxSegmentWords = 50 });

    [Theory]
    [InlineData(29, 1)]
    [InlineData(30, 2)]
    [InlineData(31, 2)]
    public void Segment_IdleGap_CutsAtThreshold(int gapMinutes, int expectedSegments)
    {
        var rows = new[] { Row(1, Day, "a"), Row(2, Day.AddMinutes(gapMinutes), "b") };

        Assert.Equal(expectedSegments, HistorySegmenter.Segment("ch", rows, Settings).Count);
    }

    [Fact]
    public void Segment_LocalMidnight_Cuts()
    {
        // 21:50Z and 22:10Z on 2026-05-04 are 23:50 and 00:10 in Copenhagen (UTC+2).
        var rows = new[]
        {
            Row(1, new DateTimeOffset(2026, 5, 4, 21, 50, 0, TimeSpan.Zero), "a"),
            Row(2, new DateTimeOffset(2026, 5, 4, 22, 10, 0, TimeSpan.Zero), "b"),
        };

        var segments = HistorySegmenter.Segment("ch", rows, Settings);

        Assert.Equal(["2026-05-04", "2026-05-05"], segments.Select(s => s.LocalDate));
    }

    [Fact]
    public void Segment_WordCap_StartsNewSegmentBeforePassingCap()
    {
        var thirtyWords = string.Join(' ', Enumerable.Repeat("word", 30));
        var rows = new[] { Row(1, Day, thirtyWords), Row(2, Day.AddMinutes(1), thirtyWords), Row(3, Day.AddMinutes(2), "short") };

        var segments = HistorySegmenter.Segment("ch", rows, Settings);

        Assert.Equal([[1L], [2L, 3L]], segments.Select(s => s.MsgIds.ToArray()));
        Assert.Equal(30, segments[0].Words);
    }

    [Fact]
    public void Segment_TransferAndToolRows_AreExcluded()
    {
        var rows = new[]
        {
            Row(1, Day, "kept"),
            Row(2, Day.AddMinutes(1), "moved", category: MessageCategory.Transfer),
            Row(3, Day.AddMinutes(2), "tool output", role: "tool"),
            Row(4, Day.AddMinutes(3), "task", category: MessageCategory.Internal),
        };

        var segment = Assert.Single(HistorySegmenter.Segment("ch", rows, Settings));

        Assert.Equal([1L, 4L], segment.MsgIds);
        Assert.True(segment.HasInternal);
    }

    [Fact]
    public void Segment_EqualTimestampsAndShuffledInput_AreOrderedByTimestampThenId()
    {
        var rows = new[] { Row(7, Day.AddMinutes(1), "c"), Row(5, Day, "a"), Row(6, Day, "b") };

        var segment = Assert.Single(HistorySegmenter.Segment("ch", rows, Settings));

        Assert.Equal([5L, 6L, 7L], segment.MsgIds);
        Assert.Equal(5, segment.FirstMsgId);
        Assert.Equal(7, segment.LastMsgId);
        Assert.Equal("a\nb\nc", segment.Body);
    }

    [Fact]
    public void Segment_BackdatedBulkInsert_SameAsInOrderInput()
    {
        var inOrder = new[] { Row(1, Day, "a"), Row(2, Day.AddMinutes(5), "b"), Row(3, Day.AddMinutes(10), "c") };
        var backdated = new[] { inOrder[0], inOrder[2], Row(4, Day.AddMinutes(5), "b") with { Id = 2 } };

        Assert.Equal(
            HistorySegmenter.Segment("ch", inOrder, Settings).Select(s => s.Fingerprint),
            HistorySegmenter.Segment("ch", backdated, Settings).Select(s => s.Fingerprint));
    }

    [Fact]
    public void Segment_RecallEchoAndAttachment_AreReflected()
    {
        var rows = new[]
        {
            Row(1, Day, "photo\n[image attached: image/jpeg]"),
            Row(2, Day.AddMinutes(1), "found it", role: "assistant", toolCalls: ToolCalls("history_search")),
        };

        var segment = Assert.Single(HistorySegmenter.Segment("ch", rows, Settings));

        Assert.True(segment.HasRecallEcho);
        Assert.Contains("[attachment:image/jpeg]", segment.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", segment.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Segment_OtherChannelRows_AreIgnored()
    {
        var rows = new[] { Row(1, Day, "a"), Row(2, Day, "b", channel: "other") };

        Assert.Equal([1L], Assert.Single(HistorySegmenter.Segment("ch", rows, Settings)).MsgIds);
    }
}
