using Cortex.Contained.Agent.Host.Agent;

namespace Cortex.Contained.Agent.Host.Tests.Agent;

public sealed class MemoryExtractionBufferTests
{
    // ── Append + Drain ────────────────────────────────────────────────────

    [Fact]
    public void Drain_ReturnsAppendedEntries_ThenClears()
    {
        var buffer = new MemoryExtractionBuffer();
        buffer.Append(Entry("user", "hello"));
        buffer.Append(Entry("assistant", "world"));

        var drained = buffer.Drain();

        Assert.Equal(2, drained.Count);
        Assert.Equal("hello", drained[0].Content);
        Assert.Equal("world", drained[1].Content);

        // Buffer is now empty.
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Drain());
    }

    [Fact]
    public void Drain_ReturnsEmpty_WhenNothingAppended()
    {
        var buffer = new MemoryExtractionBuffer();
        Assert.Empty(buffer.Drain());
    }

    // ── Cap at MaxSize ────────────────────────────────────────────────────

    [Fact]
    public void Append_SilentlyDropsEntriesBeyondMaxSize()
    {
        var buffer = new MemoryExtractionBuffer();
        for (var i = 0; i < MemoryExtractionBuffer.MaxSize + 10; i++)
        {
            buffer.Append(Entry("user", $"msg-{i}"));
        }

        Assert.Equal(MemoryExtractionBuffer.MaxSize, buffer.Count);
    }

    // ── PeekAll ───────────────────────────────────────────────────────────

    [Fact]
    public void PeekAll_ReturnsEntries_WithoutClearing()
    {
        var buffer = new MemoryExtractionBuffer();
        buffer.Append(Entry("user", "a"));
        buffer.Append(Entry("user", "b"));

        var peek1 = buffer.PeekAll();
        var peek2 = buffer.PeekAll();

        Assert.Equal(2, peek1.Count);
        Assert.Equal(2, peek2.Count);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void PeekAll_ReturnsEmpty_WhenBufferEmpty()
    {
        var buffer = new MemoryExtractionBuffer();
        Assert.Empty(buffer.PeekAll());
    }

    // ── Count ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_ReflectsCurrentSize()
    {
        var buffer = new MemoryExtractionBuffer();
        Assert.Equal(0, buffer.Count);

        buffer.Append(Entry("user", "x"));
        Assert.Equal(1, buffer.Count);

        buffer.Drain();
        Assert.Equal(0, buffer.Count);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static ExtractionEntry Entry(string role, string content) =>
        new()
        {
            Role = role,
            Content = content,
            Timestamp = DateTimeOffset.UtcNow,
        };
}
