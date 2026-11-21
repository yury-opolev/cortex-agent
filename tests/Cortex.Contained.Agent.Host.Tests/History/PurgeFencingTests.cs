using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Purge generation and fencing of index publications and recall (§4.1).</summary>
public sealed class PurgeFencingTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task IndexAsync()
    {
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
    }

    private HistoryRecallService Recall() => new(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor);

    private Task<HistorySearchResult> SearchAsync(string query) =>
        this.Recall().SearchAsync(new HistorySearchRequest { Query = query }, CancellationToken.None);

    [Fact]
    public async Task SearchAsync_IndexBehindPurgeGeneration_WithholdsIndexedHitsUntilApplied()
    {
        await this.h.SayAsync("zqfence alpha", 0);
        await this.h.SayAsync("other", 1, channel: "other");
        await this.IndexAsync();
        Assert.NotEmpty((await this.SearchAsync("zqfence")).Hits);

        // A purge committed in messages.db that the index has not applied yet.
        await this.h.Store.DeleteChannelMessagesAsync("other");

        var fenced = await this.SearchAsync("zqfence");
        Assert.Empty(fenced.Hits);
        Assert.True(fenced.Coverage.PurgeFenced);
        Assert.False(fenced.Coverage.IsComplete);

        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
        var applied = await this.SearchAsync("zqfence");
        Assert.NotEmpty(applied.Hits);
        Assert.False(applied.Coverage.PurgeFenced);
    }

    [Fact]
    public async Task ApplyPendingAsync_PurgeCommittedDuringBatch_DiscardsAndRecomputes()
    {
        await this.h.SayAsync("first", 0);
        await this.IndexAsync();
        await this.h.SayAsync("zqlate words", 1);
        var source = new FlakyPurgeGenerationSource(this.h.Store, staleReads: 1);
        using var projector = new HistoryProjector(source, this.h.Store, this.h.Generations, this.h.Gate, this.h.Monitor, this.h.Status, NullLogger<HistoryProjector>.Instance);
        this.h.Projector.Dispose();
        await projector.InitializeAsync(CancellationToken.None);

        await projector.ApplyPendingAsync(CancellationToken.None);

        Assert.Equal(2, source.Reads);
        using var reader = projector.Current!.OpenReader();
        Assert.Contains(reader.ReadTerms(), t => t == "zqlate");
    }

    [Fact]
    public async Task ApplyPendingAsync_AlwaysFenced_FailsWithoutPublishing()
    {
        await this.h.SayAsync("first", 0);
        await this.IndexAsync();
        await this.h.SayAsync("zqnever", 1);
        var source = new FlakyPurgeGenerationSource(this.h.Store, staleReads: int.MaxValue);
        using var projector = new HistoryProjector(source, this.h.Store, this.h.Generations, this.h.Gate, this.h.Monitor, this.h.Status, NullLogger<HistoryProjector>.Instance);
        this.h.Projector.Dispose();
        await projector.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(() => projector.ApplyPendingAsync(CancellationToken.None));

        using var reader = projector.Current!.OpenReader();
        Assert.DoesNotContain(reader.ReadTerms(), t => t == "zqnever");
    }

    [Fact]
    public async Task ApplyPendingAsync_UnknownJournalOp_ThrowsEveryTimeAndNeverAdvances()
    {
        await this.h.SayAsync("a", 0);
        await this.IndexAsync();
        var before = this.h.Projector.Current!.ReadState().CheckpointJournalSeq;
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(this.h.Root, "messages.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO HistoryJournal (Op, ChannelId, At) VALUES ('teleport', 'ch', '2026-11-21T00:00:00.0000000+00:00');";
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => this.h.Projector.ApplyPendingAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => this.h.Projector.ApplyPendingAsync(CancellationToken.None));

        Assert.Equal(before, this.h.Projector.Current!.ReadState().CheckpointJournalSeq);
    }

    [Fact]
    public void RebuildReason_AppliedPurgeGenerationAheadOfSource_RequiresRebuild()
    {
        var meta = new HistorySourceMeta("db", 0, 10, 0) { PurgeGeneration = 1 };
        var state = new ProjectorState(1, 10, "db", 0, "fp", true, "[]") { AppliedPurgeGeneration = 3 };

        Assert.Equal("source purge generation went backwards (restored database)", ProjectorStartupDecision.RebuildReason(meta, state, "fp"));
    }

    /// <summary>Reports a newer purge generation than committed for the first <c>staleReads</c> reads.</summary>
    private sealed class FlakyPurgeGenerationSource(MessageStore inner, int staleReads) : IHistorySource
    {
        public int Reads { get; private set; }

        public Task<HistorySourceSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken) => inner.OpenSnapshotAsync(cancellationToken);

        public Task PruneJournalAsync(long throughSeq, CancellationToken cancellationToken) => inner.PruneJournalAsync(throughSeq, cancellationToken);

        public async Task<long> ReadPurgeGenerationAsync(CancellationToken cancellationToken)
        {
            this.Reads++;
            var actual = await inner.ReadPurgeGenerationAsync(cancellationToken);
            return this.Reads <= staleReads ? actual + 1 : actual;
        }
    }
}
