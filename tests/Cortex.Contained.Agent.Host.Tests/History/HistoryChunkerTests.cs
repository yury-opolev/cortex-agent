using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HistoryChunkerTests
{
    private static readonly HistoryChunkSettings Small = new(ChunkWords: 10, OverlapWords: 2, MaxChars: 3000);

    private static string Words(string prefix, int count) =>
        string.Join(' ', Enumerable.Range(1, count).Select(i => prefix + i));

    [Fact]
    public void Line_LabelContentAndToolNames_NoTimestamp()
    {
        var line = HistoryTranscript.Line(Row(1, Day, "see [image attached: image/png] here", role: "assistant", toolCalls: ToolCalls("date_time")));

        Assert.Equal("assistant: see [attachment:image/png] here [tools: date_time]", line);
    }

    [Fact]
    public void Chunk_ShortSegment_OneChunkWithAllMembers()
    {
        var rows = new[] { Row(1, Day, "hello there"), Row(2, Day.AddMinutes(1), "hi", role: "assistant") };

        var chunks = HistoryChunker.Chunk(rows, Small);

        var (chunk, text) = Assert.Single(chunks);
        Assert.Equal(0, chunk.Ordinal);
        Assert.Equal([1L, 2L], chunk.MsgIds);
        Assert.Equal("user: hello there\nassistant: hi", text);
        Assert.Equal(HistoryChunker.Hash(text), chunk.TextHash);
        Assert.Equal(5, chunk.Words);
    }

    [Fact]
    public void Chunk_LongMessage_OverlapsByConfiguredWords()
    {
        var rows = new[] { Row(1, Day, Words("w", 25)) };

        var chunks = HistoryChunker.Chunk(rows, Small);

        Assert.True(chunks.Count >= 3);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Chunk.Ordinal));
        var firstTail = chunks[0].Text.Split(' ')[^2..];
        var secondHead = chunks[1].Text.Split(' ')[..2];
        Assert.Equal(firstTail, secondHead);
        Assert.All(chunks, c => Assert.True(c.Chunk.Words <= Small.ChunkWords));
        Assert.EndsWith("w25", chunks[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Chunk_PrefersMessageBoundaryInSecondHalf()
    {
        var rows = new[] { Row(1, Day, Words("a", 6)), Row(2, Day.AddMinutes(1), Words("b", 8)) };

        var chunks = HistoryChunker.Chunk(rows, Small);

        Assert.Equal([1L], chunks[0].Chunk.MsgIds);
        Assert.Contains(2L, chunks[1].Chunk.MsgIds);
    }

    [Fact]
    public void Chunk_HugeToken_SplitUnderCharCap()
    {
        var settings = new HistoryChunkSettings(ChunkWords: 256, OverlapWords: 32, MaxChars: 400);
        var rows = new[] { Row(1, Day, new string('x', 5000)) };

        var chunks = HistoryChunker.Chunk(rows, settings);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= settings.MaxChars, $"{c.Text.Length} chars"));
    }

    [Fact]
    public void Chunk_SameRows_SameHashes_ContentChange_NewHash()
    {
        var rows = new[] { Row(1, Day, Words("a", 30)), Row(2, Day.AddMinutes(1), "tail") };
        var edited = new[] { rows[0], Row(2, Day.AddMinutes(1), "tale") };

        var first = HistoryChunker.Chunk(rows, Small).Select(c => c.Chunk.TextHash).ToList();
        var again = HistoryChunker.Chunk(rows, Small).Select(c => c.Chunk.TextHash).ToList();
        var changed = HistoryChunker.Chunk(edited, Small).Select(c => c.Chunk.TextHash).ToList();

        Assert.Equal(first, again);
        Assert.Equal(first[..^1], changed[..^1]);
        Assert.NotEqual(first[^1], changed[^1]);
    }

    [Fact]
    public void Chunk_InternalOnlyChunk_IsFlagged()
    {
        var rows = new[] { Row(1, Day, "task instruction", role: "assistant", category: MessageCategory.Internal) };
        var mixed = new[] { rows[0], Row(2, Day.AddMinutes(1), "hello") };

        Assert.True(HistoryChunker.Chunk(rows, Small)[0].Chunk.AllInternal);
        Assert.False(HistoryChunker.Chunk(mixed, Small)[0].Chunk.AllInternal);
    }
}
