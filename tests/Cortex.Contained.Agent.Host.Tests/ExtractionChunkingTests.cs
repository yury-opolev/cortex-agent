using Cortex.Contained.Agent.Host.Agent;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests;

public class ExtractionChunkingTests
{
    private static ExtractionEntry MakeEntry(string role, string content) => new()
    {
        Role = role,
        Content = content,
        Timestamp = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void ChunkEntries_SmallBuffer_SingleChunk()
    {
        var entries = new List<ExtractionEntry>
        {
            MakeEntry("user", "hello"),
            MakeEntry("assistant", "hi there"),
        };

        var chunks = MemoryExtractionService.ChunkEntries(entries);

        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].Count);
    }

    [Fact]
    public void ChunkEntries_EmptyBuffer_SingleEmptyChunk()
    {
        var entries = Array.Empty<ExtractionEntry>();

        var chunks = MemoryExtractionService.ChunkEntries(entries);

        Assert.Single(chunks);
    }

    [Fact]
    public void ChunkEntries_LargeBuffer_MultipleChunks()
    {
        // Create entries that exceed 80K chars total
        var entries = new List<ExtractionEntry>();
        for (int i = 0; i < 20; i++)
        {
            entries.Add(MakeEntry("user", new string('a', 5000)));
            entries.Add(MakeEntry("assistant", new string('b', 5000)));
        }

        // 20 pairs * 10K chars = 200K > 80K threshold
        var chunks = MemoryExtractionService.ChunkEntries(entries);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    [Fact]
    public void ChunkEntries_ChunksOverlapByFourEntries()
    {
        // Create entries that will produce at least 2 chunks
        var entries = new List<ExtractionEntry>();
        for (int i = 0; i < 20; i++)
        {
            entries.Add(MakeEntry("user", new string((char)('a' + i % 26), 5000)));
            entries.Add(MakeEntry("assistant", new string((char)('A' + i % 26), 5000)));
        }

        var chunks = MemoryExtractionService.ChunkEntries(entries);

        if (chunks.Count >= 2)
        {
            // Last 4 entries of chunk[0] should equal first 4 entries of chunk[1]
            var chunk0Tail = chunks[0].TakeLast(4).ToList();
            var chunk1Head = chunks[1].Take(4).ToList();

            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(chunk0Tail[i].Content, chunk1Head[i].Content);
            }
        }
    }

    [Fact]
    public void ChunkEntries_AllEntriesCovered()
    {
        var entries = new List<ExtractionEntry>();
        for (int i = 0; i < 30; i++)
        {
            entries.Add(MakeEntry("user", $"msg-{i}-" + new string('x', 4000)));
        }

        var chunks = MemoryExtractionService.ChunkEntries(entries);

        // Every original entry should appear in at least one chunk
        var allChunkedContents = chunks.SelectMany(c => c).Select(e => e.Content).ToHashSet();
        foreach (var entry in entries)
        {
            Assert.Contains(entry.Content, allChunkedContents);
        }
    }
}
