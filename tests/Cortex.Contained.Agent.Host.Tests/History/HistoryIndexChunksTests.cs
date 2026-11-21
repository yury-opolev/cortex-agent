using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Chunk rows are the embedding job queue: written in the batch transaction, fenced on publish.</summary>
public sealed class HistoryIndexChunksTests : IDisposable
{
    private static readonly SegmenterSettings Dense = new(TimeZoneInfo.Utc, TimeSpan.FromMinutes(30), 1500)
    {
        Chunking = new HistoryChunkSettings(256, 32, 3000),
    };

    private static readonly TimeSpan OpenDelay = TimeSpan.FromMinutes(2);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"history-chunks-{Guid.NewGuid():N}");
    private readonly HistoryIndexGenerations generations;
    private readonly HistoryIndexDatabase database;

    public HistoryIndexChunksTests()
    {
        this.generations = new HistoryIndexGenerations(this.dir);
        this.database = this.generations.Create(1, new ProjectorState(1, 0, "instance", 0, "fp", false, "[]"));
    }

    public void Dispose()
    {
        this.database.Dispose();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private static IReadOnlyList<HistorySegment> Segments(params MessageRecord[] rows) => HistorySegmenter.Segment("ch", rows, Dense);

    private static IndexBatch Batch(IReadOnlyList<HistorySegment> segments, DateTimeOffset now) =>
        new(new Dictionary<string, IReadOnlyList<HistorySegment>> { ["ch"] = segments }) { Now = now };

    private static float[] Vector(params float[] values) => values;

    [Fact]
    public void Apply_DenseSegments_QueuesChunksAndMarksOnlyLastSegmentOpen()
    {
        this.database.Apply(Batch(Segments(Row(1, Day, "morning"), Row(2, Day.AddHours(2), "evening")), Day));

        using var reader = this.database.OpenReader();
        var chunks = reader.ReadChunks();
        Assert.Equal([1L, 2L], chunks.Select(c => c.SegFirstMsgId));
        Assert.Equal([false, true], chunks.Select(c => c.IsOpen));
        Assert.Equal(new ChunkCounts(2, 0, 2, 0, Day), reader.ReadChunkCounts());
    }

    [Fact]
    public void ReadPendingChunks_OpenSegmentWaitsForDebounce()
    {
        this.database.Apply(Batch(Segments(Row(1, Day, "morning"), Row(2, Day.AddHours(2), "evening")), Day));

        var now = this.database.ReadPendingChunks(10, Day, OpenDelay, 10);
        var later = this.database.ReadPendingChunks(10, Day + OpenDelay, OpenDelay, 10);

        Assert.Equal([1L], now.Select(p => p.SegmentMsgIds[0]));
        Assert.Equal([1L, 2L], later.Select(p => p.SegmentMsgIds[0]).Order());
    }

    [Fact]
    public void ReadPendingChunks_OpenSegmentEligibleAfterEnoughNewMessages()
    {
        var rows = new List<MessageRecord> { Row(1, Day, "start") };
        this.database.Apply(Batch(Segments([.. rows]), Day));
        for (var i = 2; i <= 11; i++)
        {
            rows.Add(Row(i, Day.AddMinutes(i), "more " + i));
            this.database.Apply(Batch(Segments([.. rows]), Day.AddMinutes(i)));
            Assert.Equal(i < 11, this.database.ReadPendingChunks(10, Day.AddMinutes(1), OpenDelay, 10).Count == 0);
        }
    }

    [Fact]
    public void PublishVectors_MatchingHashOnly_AndReappliedChunkKeepsVector()
    {
        this.database.Apply(Batch(Segments(Row(1, Day, "morning"), Row(2, Day.AddHours(2), "evening")), Day));
        var pending = this.database.ReadPendingChunks(10, Day + OpenDelay, OpenDelay, 10);

        Assert.Equal(0, this.database.PublishVectors([("stale-hash", Vector(1, 0))]));
        Assert.Equal(2, this.database.PublishVectors(pending.Select(p => (p.TextHash, Vector(3, 4))).ToList()));
        this.database.Apply(Batch(Segments(Row(1, Day, "morning"), Row(2, Day.AddHours(2), "evening!")), Day.AddHours(3)));

        using var reader = this.database.OpenReader();
        Assert.Equal([true, false], reader.ReadChunks().Select(c => c.HasVector));
        Assert.Equal([0.6f, 0.8f], reader.ReadVector(pending[0].TextHash)!);
    }

    [Fact]
    public void RecordEmbeddingFailure_ThreeTimes_ReportsFailedAndStopsRetrying()
    {
        this.database.Apply(Batch(Segments(Row(1, Day, "morning"), Row(2, Day.AddHours(2), "evening")), Day));
        var first = this.database.ReadPendingChunks(10, Day, OpenDelay, 10).Single();

        for (var i = 0; i < HistoryIndexDatabase.MaxEmbeddingAttempts; i++)
        {
            this.database.RecordEmbeddingFailure([first.ChunkId]);
        }

        Assert.Empty(this.database.ReadPendingChunks(10, Day, OpenDelay, 10));
        Assert.Equal(1, this.database.ReadChunkCounts().Failed);
    }
}
