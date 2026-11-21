using Cortex.Contained.Agent.Host.History;
using Microsoft.Extensions.Logging;

namespace Cortex.Contained.Agent.Host.Tests.History;

public sealed class HistoryIndexDatabaseTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), $"history-index-{Guid.NewGuid():N}");
    private readonly HistoryIndexGenerations generations;
    private readonly HistoryIndexDatabase database;

    public HistoryIndexDatabaseTests()
    {
        this.generations = new HistoryIndexGenerations(this.dir);
        this.database = HistoryIndexDatabase.Create(
            this.generations.PathFor(1),
            new ProjectorState(1, 0, "instance", 0, "fp", false, "[]"));
    }

    public void Dispose()
    {
        this.database.Dispose();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private static HistorySegment Segment(string channel, string start, long firstId, string body) =>
        new(channel, start, start, start[..10], firstId, firstId, [firstId], 2, false, false, "fp" + firstId, body);

    private void Seed()
    {
        this.database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>>
        {
            ["a"] = [Segment("a", "2026-05-04T08:00:00.0000000Z", 1, "night train oslo"), Segment("a", "2026-05-05T08:00:00.0000000Z", 3, "oslo oslo ferry")],
            ["b"] = [Segment("b", "2026-05-04T09:00:00.0000000Z", 2, "kaffe")],
        })
        { CheckpointSeq = 7 });
    }

    [Fact]
    public void OpenReader_HeldLongerThanTheLimit_LogsAWarningOnDispose()
    {
        var logger = new RecordingLogger();
        using var logged = new HistoryIndexGenerations(Path.Combine(this.dir, "logged"), logger: logger).Create(1, new ProjectorState(1, 0, "instance", 0, "fp", false, "[]"));
        logged.ReaderWarnAfter = TimeSpan.Zero;

        logged.OpenReader().Dispose();

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
    }

    [Fact]
    public void OpenReader_ShortLivedReader_LogsNothing()
    {
        var logger = new RecordingLogger();
        using var quiet = new HistoryIndexGenerations(Path.Combine(this.dir, "quiet"), logger: logger).Create(1, new ProjectorState(1, 0, "instance", 0, "fp", false, "[]"));

        quiet.OpenReader().Dispose();

        Assert.Empty(logger.Levels);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            this.Levels.Add(logLevel);
    }
    [Fact]
    public void Create_FailsMidway_LeavesNoGenerationFileBehind()
    {
        var failing = new ProjectorState(2, 0, null!, 0, "fp", false, "[]");

        Assert.ThrowsAny<Exception>(() => this.generations.Create(2, failing));

        Assert.Equal([1L], this.generations.List());
        Assert.Empty(Directory.EnumerateFiles(this.dir, "*creating*"));
    }

    [Fact]
    public void Apply_UpdatesCheckpointCoverageAndVocabulary()
    {
        this.Seed();

        using var reader = this.database.OpenReader();
        Assert.Equal(7, reader.ReadState().CheckpointJournalSeq);
        Assert.Equal(3, reader.SegmentCount());
        Assert.Equal(2, reader.DocumentFrequency("oslo"));
        Assert.Equal(["a", "b"], reader.ReadCoverage().Select(c => c.ChannelId));
        Assert.Equal("2026-05-05T08:00:00.0000000Z", reader.ReadCoverage()[0].To);
    }

    [Fact]
    public void Apply_ChannelReplacement_RemovesOldPostings()
    {
        this.Seed();

        this.database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>> { ["a"] = [] }));

        using var reader = this.database.OpenReader();
        Assert.Equal(0, reader.DocumentFrequency("oslo"));
        Assert.Equal(["kaffe"], reader.ReadTerms());
        Assert.DoesNotContain(reader.ReadPostings(), p => p.StartsWith("oslo", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchRelevance_RanksByBm25AndFilters()
    {
        this.Seed();

        using var reader = this.database.OpenReader();
        var all = reader.SearchRelevance("\"oslo\"", new SegmentFilter(null, null, null), 10, 0);
        var later = reader.SearchRelevance("\"oslo\"", new SegmentFilter("a", "2026-05-05T00:00:00.0000000Z", null), 10, 0);

        Assert.Equal([3L, 1L], all.Select(s => s.FirstMsgId));
        Assert.True(all[0].Score < all[1].Score);
        Assert.Equal([3L], later.Select(s => s.FirstMsgId));
    }

    [Fact]
    public void SearchTimeOrder_KeysetPagination()
    {
        this.Seed();

        using var reader = this.database.OpenReader();
        var first = reader.SearchTimeOrder("\"oslo\" OR \"kaffe\"", new SegmentFilter(null, null, null), 2, null);
        var rest = reader.SearchTimeOrder("\"oslo\" OR \"kaffe\"", new SegmentFilter(null, null, null), 2, (first[^1].StartTs, first[^1].SegId));

        Assert.Equal([1L, 2L], first.Select(s => s.FirstMsgId));
        Assert.Equal([3L], rest.Select(s => s.FirstMsgId));
    }

    [Fact]
    public void Prefix_ThreeLetterStem_Matches()
    {
        this.Seed();

        using var reader = this.database.OpenReader();
        Assert.Equal(2, reader.SearchRelevance("\"osl\"*", new SegmentFilter(null, null, null), 10, 0).Count);
    }

    [Fact]
    public void BuildProgress_MarkComplete_ClearsProgress()
    {
        this.database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>>()) { BuildProgress = ["a"] });
        Assert.Equal(["a"], this.database.ReadBuildProgress());

        this.database.Apply(new IndexBatch(new Dictionary<string, IReadOnlyList<HistorySegment>>()) { MarkBuildComplete = true });

        Assert.Empty(this.database.ReadBuildProgress());
        Assert.True(this.database.ReadState().BuildComplete);
    }

    [Fact]
    public void Generations_PointerAndDelete()
    {
        Assert.Null(this.generations.ReadCurrent());
        this.generations.WriteCurrent(1);

        Assert.Equal(1, this.generations.ReadCurrent());
        Assert.Equal([1L], this.generations.List());
        Assert.Equal(2, this.generations.Next());

        this.database.Dispose();
        Assert.True(this.generations.Delete(1));
        Assert.Empty(this.generations.List());
    }
}
