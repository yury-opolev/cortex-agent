using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>
/// The writer boundary (§8): every Messages mutation journals in the same transaction, and
/// reads use separate connections.
/// </summary>
public sealed class MessageStoreJournalTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string tempDir;
    private readonly MessageStore store;

    public MessageStoreJournalTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), $"msgstore-journal-{Guid.NewGuid():N}");
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<(HistorySourceMeta Meta, IReadOnlyList<HistoryJournalEntry> Journal)> ReadJournalAsync()
    {
        await using var snapshot = await this.store.OpenSnapshotAsync(CancellationToken.None);
        return (await snapshot.ReadMetaAsync(CancellationToken.None), await snapshot.ReadJournalAsync(0, 1000, CancellationToken.None));
    }

    [Fact]
    public async Task Constructor_NewDatabase_InitializesMeta()
    {
        var (meta, journal) = await this.ReadJournalAsync();

        Assert.Equal(32, meta.DbInstanceId.Length);
        Assert.Equal(0, meta.RestoreGeneration);
        Assert.Equal(0, meta.JournalHead);
        Assert.Empty(journal);
    }

    [Fact]
    public async Task SaveMessageAsync_AppendsUpsertAndAdvancesHead()
    {
        var id = await this.store.SaveMessageAsync("u", "ch", "user", "hello", T0);

        var (meta, journal) = await this.ReadJournalAsync();

        var entry = Assert.Single(journal);
        Assert.Equal(HistoryJournalOp.Upsert, entry.Op);
        Assert.Equal("ch", entry.ChannelId);
        Assert.Equal([id], entry.MessageIds);
        Assert.Equal(entry.Seq, meta.JournalHead);
    }

    [Fact]
    public async Task UpdateContentAndToolCalls_ExistingRow_AppendUpserts()
    {
        var id = await this.store.SaveMessageAsync("u", "ch", "assistant", "meet at 10", T0);

        await this.store.UpdateContentAsync(id, "meet at 11");
        await this.store.UpdateToolCallsAsync(id, "[]");

        var (_, journal) = await this.ReadJournalAsync();
        Assert.Equal(3, journal.Count);
        Assert.All(journal, e => Assert.Equal([id], e.MessageIds));
    }

    [Fact]
    public async Task UpdateContentAsync_MissingRow_DoesNotJournal()
    {
        await this.store.UpdateContentAsync(404, "nothing");

        var (_, journal) = await this.ReadJournalAsync();
        Assert.Empty(journal);
    }

    [Fact]
    public async Task BulkInsertAsync_AppendsOneUpsertPerChannel()
    {
        MessageRecord Record(string channel, string text) => new()
        {
            UserId = "u", ChannelId = channel, Role = "user", Content = text, Timestamp = T0,
        };

        var inserted = await this.store.BulkInsertAsync([Record("a", "1"), Record("b", "2"), Record("a", "3")]);

        var (_, journal) = await this.ReadJournalAsync();
        Assert.Equal(3, inserted);
        Assert.Equal(2, journal.Count);
        Assert.Equal(2, journal.Single(e => e.ChannelId == "a").MessageIds.Count);
    }

    [Fact]
    public async Task SaveMessageAsync_FailureAfterRowWrite_RollsBackRowAndJournal()
    {
        this.store.FaultInjector = stage => throw new InvalidOperationException(stage);

        await Assert.ThrowsAsync<InvalidOperationException>(() => this.store.SaveMessageAsync("u", "ch", "user", "x", T0));

        this.store.FaultInjector = null;
        var (meta, journal) = await this.ReadJournalAsync();
        Assert.Empty(journal);
        Assert.Equal(0, meta.JournalHead);
        Assert.Equal(0, await this.store.GetTotalMessageCountAsync());
    }

    [Fact]
    public async Task PurgeAsync_FailureAfterDelete_RollsBackEverything()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "keep me", T0);
        this.store.FaultInjector = stage => { if (stage == "after-purge-delete") { throw new InvalidOperationException(stage); } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => this.store.DeleteAllMessagesAsync());

        this.store.FaultInjector = null;
        Assert.Equal(1, await this.store.GetTotalMessageCountAsync());
        var (_, journal) = await this.ReadJournalAsync();
        Assert.Single(journal);
    }

    [Theory]
    [InlineData("ch", false, 2)]
    [InlineData(null, true, 2)]
    [InlineData("ch", true, 1)]
    [InlineData(null, false, 3)]
    public async Task DeletePaths_AppendPurgeWithScope(string? channel, bool olderThan, int expectedDeleted)
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "old", T0);
        await this.store.SaveMessageAsync("u", "other", "user", "old", T0);
        await this.store.SaveMessageAsync("u", "ch", "user", "new", T0.AddDays(2));
        var cutoff = T0.AddDays(1);

        var deleted = (channel, olderThan) switch
        {
            ("ch", false) => await this.store.DeleteChannelMessagesAsync("ch"),
            ("ch", true) => await this.store.DeleteChannelMessagesOlderThanAsync("ch", cutoff),
            (null, true) => await this.store.DeleteMessagesOlderThanAsync(cutoff),
            _ => await this.store.DeleteAllMessagesAsync(),
        };

        var (_, journal) = await this.ReadJournalAsync();
        var purge = journal[^1];
        Assert.Equal(expectedDeleted, deleted);
        Assert.Equal(HistoryJournalOp.Purge, purge.Op);
        Assert.Equal(channel, purge.ChannelId);
        Assert.Equal(olderThan ? cutoff : null, purge.ToTs);
    }

    [Fact]
    public async Task PurgeAsync_WithReplacement_InsertsJournalsAndBumpsRestoreGeneration()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "old", T0);
        var replacement = new MessageRecord { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = T0 };

        var outcome = await this.store.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All) { Replacement = [replacement] }, CancellationToken.None);

        var (meta, journal) = await this.ReadJournalAsync();
        Assert.Equal((1, 1), (outcome.Deleted, outcome.Inserted));
        Assert.Equal(1, meta.RestoreGeneration);
        Assert.Equal([HistoryJournalOp.Upsert, HistoryJournalOp.Purge, HistoryJournalOp.Upsert], journal.Select(e => e.Op));
        var messages = await this.store.GetMessagesAsync("ch");
        Assert.Equal("imported", Assert.Single(messages).Content);
    }

    [Fact]
    public async Task PurgeAsync_WithSteps_RecordsPendingErasureAndWindows()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.SaveMessageAsync("u", "ch", "user", "b", T0.AddHours(10));
        var request = new MessagePurgeRequest(new HistoryPurgeScope("ch", null))
        {
            Now = T0.AddDays(5),
            PendingSteps = ErasureSteps.ForPurge(new HistoryPurgeScope("ch", null)),
        };

        var outcome = await this.store.PurgeAsync(request, CancellationToken.None);

        var task = Assert.Single(await this.store.ReadPendingErasureAsync(CancellationToken.None));
        Assert.Equal(request.ErasureId, task.Id);
        Assert.Equal((T0, T0.AddHours(10)), (task.RangeFrom!.Value, task.RangeTo!.Value));
        Assert.Contains(ErasureSteps.ToolOutput, task.Steps);
        Assert.Equal(2, outcome.Windows.Count);
    }

    [Fact]
    public async Task CompleteErasureStepAsync_LastStep_DropsTask()
    {
        await this.store.AppendErasureTaskAsync(
            new ErasureTask { Id = "t1", CreatedAt = T0, Steps = [ErasureSteps.Index, ErasureSteps.MessagesWal] },
            CancellationToken.None);

        await this.store.CompleteErasureStepAsync("t1", ErasureSteps.Index, CancellationToken.None);
        Assert.Equal([ErasureSteps.MessagesWal], Assert.Single(await this.store.ReadPendingErasureAsync(CancellationToken.None)).Steps);

        await this.store.CompleteErasureStepAsync("t1", ErasureSteps.MessagesWal, CancellationToken.None);
        Assert.Empty(await this.store.ReadPendingErasureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OpenSnapshotAsync_LaterWrites_AreInvisibleToSnapshot()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "first", T0);
        await using var snapshot = await this.store.OpenSnapshotAsync(CancellationToken.None);

        await this.store.SaveMessageAsync("u", "ch", "user", "second", T0.AddMinutes(1));

        Assert.Single(await snapshot.ReadChannelAsync("ch", CancellationToken.None));
        Assert.Equal(2, await this.store.GetTotalMessageCountAsync());
    }

    [Fact]
    public async Task PruneJournalAsync_AdvancesPrunedThroughAndDeletesRows()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.SaveMessageAsync("u", "ch", "user", "b", T0);

        await this.store.PruneJournalAsync(1, CancellationToken.None);

        var (meta, journal) = await this.ReadJournalAsync();
        Assert.Equal(1, meta.JournalPrunedThrough);
        Assert.Equal([2L], journal.Select(e => e.Seq));
    }

    [Fact]
    public async Task Mutations_WithConcurrentReaders_NeverFail()
    {
        var id = await this.store.SaveMessageAsync("u", "ch", "user", "seed", T0);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await using var snapshot = await this.store.OpenSnapshotAsync(CancellationToken.None);
                await snapshot.ReadChannelAsync("ch", CancellationToken.None);
                await this.store.GetMessagesAsync("ch");
            }
        })).ToList();

        for (var i = 0; i < 50; i++)
        {
            var next = await this.store.SaveMessageAsync("u", "ch", "user", $"m{i}", T0.AddMinutes(i));
            await this.store.UpdateContentAsync(next, $"edited {i}");
            await this.store.UpdateToolCallsAsync(id, "[]");
            await this.store.DeleteChannelMessagesOlderThanAsync("ch", T0.AddMinutes(i - 10));
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);
        Assert.True(await this.store.GetTotalMessageCountAsync() > 0);
    }

    [Fact]
    public async Task InMemoryStore_ReadersSeeWriterData()
    {
        await using var memory = new MessageStore(":memory:", NullLogger<MessageStore>.Instance);

        await memory.SaveMessageAsync("u", "ch", "user", "hi", T0, category: MessageCategory.Normal);

        Assert.Single(await memory.GetMessagesAsync("ch"));
    }
}
