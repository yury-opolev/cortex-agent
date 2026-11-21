using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Exact dense search: filters before scoring, brute-force equal, int8 copy plus rescore above the threshold.</summary>
public sealed class HistoryDenseSearchTests : IDisposable
{
    private static readonly SegmenterSettings Dense = new(TimeZoneInfo.Utc, TimeSpan.FromMinutes(30), 1500)
    {
        Chunking = new HistoryChunkSettings(256, 32, 3000),
    };

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"history-dense-{Guid.NewGuid():N}");
    private readonly HistoryIndexDatabase database;
    private readonly Dictionary<long, float[]> vectors = [];

    public HistoryDenseSearchTests()
    {
        this.database = new HistoryIndexGenerations(this.dir).Create(1, new ProjectorState(1, 0, "instance", 0, "fp", false, "[]"));
    }

    public void Dispose()
    {
        this.database.Dispose();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>One segment per message (hours apart) in <paramref name="channel"/>, each with its own vector.</summary>
    private void Seed(string channel, long firstId, List<float[]> segmentVectors, MessageCategory category = MessageCategory.Normal)
    {
        var rows = segmentVectors.Select((_, i) => Row(firstId + i, Day.AddHours(i * 3), $"{channel} message {firstId + i}", channel: channel, category: category)).ToArray();
        var segments = HistorySegmenter.Segment(channel, rows, Dense);
        this.database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>> { [channel] = segments }));
        for (var i = 0; i < segments.Count; i++)
        {
            this.database.PublishVectors([(segments[i].Chunks[0].TextHash, segmentVectors[i])]);
            this.vectors[segments[i].FirstMsgId] = VectorMath.Normalize(segmentVectors[i]);
        }
    }

    private static float[] Vec(params float[] values) => values;

    [Fact]
    public void SearchDense_EqualsBruteForce()
    {
        var random = new Random(7);
        var list = Enumerable.Range(0, 40).Select(_ => Enumerable.Range(0, 8).Select(_ => (float)random.NextDouble() - 0.5f).ToArray()).ToList();
        this.Seed("a", 1, list);
        var query = Enumerable.Range(0, 8).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();

        using var reader = this.database.OpenReader();
        var hits = reader.SearchDense(query, new DenseFilter(null, null, null, false), 5, int8Threshold: 1000, oversample: 4);

        var unit = VectorMath.Normalize(query);
        var expected = this.vectors.OrderByDescending(kv => VectorMath.Dot(unit, kv.Value)).Take(5).Select(kv => kv.Key);
        Assert.Equal(expected, hits.Select(h => h.MsgIds[0]));
        Assert.True(hits.Zip(hits.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    [Fact]
    public void SearchDense_SelectiveFilterAppliedBeforeScoring()
    {
        this.Seed("a", 1, Enumerable.Range(0, 50).Select(_ => Vec(1, 0)).ToList());
        this.Seed("b", 100, [Vec(0.2f, 1)]);

        using var reader = this.database.OpenReader();
        var hits = reader.SearchDense(Vec(1, 0), new DenseFilter("b", null, null, false), 1, int8Threshold: 1000, oversample: 4);

        Assert.Equal(100L, Assert.Single(hits).MsgIds[0]);
    }

    [Fact]
    public void SearchDense_TimeFilterOnSegments()
    {
        this.Seed("a", 1, [Vec(1, 0), Vec(1, 0.1f), Vec(1, 0.2f)]);

        using var reader = this.database.OpenReader();
        var since = SqliteDateTimeText.Format(Day.AddHours(3));
        var until = SqliteDateTimeText.Format(Day.AddHours(4));
        var hits = reader.SearchDense(Vec(1, 0), new DenseFilter(null, since, until, false), 5, 1000, 4);

        Assert.Equal([2L], hits.Select(h => h.MsgIds[0]));
    }

    [Fact]
    public void SearchDense_InternalOnlyChunks_ExcludedUnlessRequested()
    {
        this.Seed("a", 1, [Vec(1, 0)], MessageCategory.Internal);

        using var reader = this.database.OpenReader();
        Assert.Empty(reader.SearchDense(Vec(1, 0), new DenseFilter(null, null, null, false), 5, 1000, 4));
        Assert.Single(reader.SearchDense(Vec(1, 0), new DenseFilter(null, null, null, true), 5, 1000, 4));
    }

    [Fact]
    public void SearchDense_AboveInt8Threshold_RescoredOrderMatchesFloat()
    {
        var random = new Random(11);
        this.Seed("a", 1, Enumerable.Range(0, 60).Select(_ => Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble() - 0.5f).ToArray()).ToList());
        var query = Enumerable.Range(0, 32).Select(_ => (float)random.NextDouble() - 0.5f).ToArray();

        using var reader = this.database.OpenReader();
        var exact = reader.SearchDense(query, new DenseFilter(null, null, null, false), 5, int8Threshold: 1000, oversample: 4);
        var quantized = reader.SearchDense(query, new DenseFilter(null, null, null, false), 5, int8Threshold: 10, oversample: 4);

        Assert.Equal(exact.Select(h => h.ChunkId), quantized.Select(h => h.ChunkId));
        Assert.Equal(exact.Select(h => h.Score), quantized.Select(h => h.Score));
    }
}
