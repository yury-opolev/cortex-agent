using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;
using static Cortex.Contained.Agent.Host.Tests.History.HistoryRows;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Schema v3 index content: stemmed shadow column, trigram body and transcript chunks.</summary>
public sealed class HistoryIndexVariantsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"history-variants-{Guid.NewGuid():N}");
    private readonly HistoryIndexDatabase database;

    public HistoryIndexVariantsTests()
    {
        var generations = new HistoryIndexGenerations(this.dir);
        this.database = generations.Create(1, new ProjectorState(1, 0, "instance", 0, "fp", false, "[]"));
    }

    public void Dispose()
    {
        this.database.Dispose();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private static SegmenterSettings Settings(HistoryKeywordVariant variant, bool dense = false) =>
        new(TimeZoneInfo.Utc, TimeSpan.FromMinutes(30), 1500)
        {
            KeywordVariant = variant,
            Chunking = dense ? new HistoryChunkSettings(256, 32, 3000) : null,
        };

    [Fact]
    public void Segment_PlainVariant_NoShadowBodiesOrChunks()
    {
        var segment = Assert.Single(HistorySegmenter.Segment("ch", [Row(1, Day, "Поездом в Oslo")], Settings(HistoryKeywordVariant.Plain)));

        Assert.Null(segment.StemBody);
        Assert.Null(segment.TrigramBody);
        Assert.Empty(segment.Chunks);
    }

    [Fact]
    public void Segment_StemmedAndDense_ProducesStemBodyAndChunks()
    {
        var rows = new[] { Row(1, Day, "Поездом в Oslo"), Row(2, Day.AddMinutes(1), "ok", role: "assistant", category: MessageCategory.Proactive) };

        var segment = Assert.Single(HistorySegmenter.Segment("ch", rows, Settings(HistoryKeywordVariant.Stemmed, dense: true)));

        Assert.Equal("поезд в oslo ok", segment.StemBody);
        var chunk = Assert.Single(segment.Chunks);
        Assert.Equal([1L, 2L], chunk.MsgIds);
        Assert.Equal(HistoryChunker.Hash("user: Поездом в Oslo\ndelivered: ok"), chunk.TextHash);
    }

    [Fact]
    public void Apply_StemColumn_SearchableButNotCountedAsBodyTerm()
    {
        var segment = Assert.Single(HistorySegmenter.Segment("ch", [Row(1, Day, "Поездом")], Settings(HistoryKeywordVariant.Stemmed)));
        this.database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>> { ["ch"] = [segment] }));

        using var reader = this.database.OpenReader();
        Assert.Single(reader.SearchRelevance("stem : \"поезд\"", new SegmentFilter(null, null, null), 10, 0));
        Assert.Empty(reader.SearchRelevance("body : \"поезд\"", new SegmentFilter(null, null, null), 10, 0));
        Assert.Equal(0, reader.DocumentFrequency("поезд"));
        Assert.Equal(1, reader.DocumentFrequency("поездом"));
    }

    [Fact]
    public void Fingerprint_CoversVariantChunkingAndEmbedder()
    {
        var plain = new HistoryOptions { Dense = { Enabled = false } };
        var stemmed = new HistoryOptions { KeywordVariant = HistoryKeywordVariant.Stemmed, Dense = { Enabled = false } };
        var dense = new HistoryOptions();
        dense.Dense.Enabled = true;
        var denseOtherChunks = new HistoryOptions();
        denseOtherChunks.Dense.Enabled = true;
        denseOtherChunks.Dense.ChunkWords = 128;
        var ranking = new HistoryOptions { Dense = { Enabled = false } };
        ranking.Ranking.RrfK = 5;

        var baseline = HistoryConfigFingerprint.Compute(plain);

        Assert.NotEqual(baseline, HistoryConfigFingerprint.Compute(stemmed));
        Assert.NotEqual(baseline, HistoryConfigFingerprint.Compute(dense, "m1"));
        Assert.NotEqual(HistoryConfigFingerprint.Compute(dense, "m1"), HistoryConfigFingerprint.Compute(dense, "m2"));
        Assert.NotEqual(HistoryConfigFingerprint.Compute(dense, "m1"), HistoryConfigFingerprint.Compute(denseOtherChunks, "m1"));
        Assert.Equal(baseline, HistoryConfigFingerprint.Compute(ranking));
        Assert.Equal(baseline, HistoryConfigFingerprint.Compute(plain, "ignored-without-dense"));
    }
}
