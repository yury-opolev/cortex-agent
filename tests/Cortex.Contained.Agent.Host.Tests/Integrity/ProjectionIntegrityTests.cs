using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Integrity;

/// <summary>Purge during rebuild, restore, and incremental-equals-rebuild with the event log in play.</summary>
[Trait("Category", "Integrity")]
public sealed class ProjectionIntegrityTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    [Fact]
    public async Task PurgeCommittedDuringRebuild_NewGenerationHoldsNoPurgedTerms()
    {
        for (var i = 0; i < 30; i++)
        {
            await this.h.SayAsync($"zqpurgeme{i % 3} filler", i, channel: "gone");
            await this.h.SayAsync($"keepme{i % 3} filler", i, channel: "kept");
        }

        // The purge commits right after the build pinned its read snapshot.
        var purged = false;
        var source = new SnapshotHookSource(this.h.Store, async () =>
        {
            if (!purged)
            {
                purged = true;
                await this.h.Store.DeleteChannelMessagesAsync("gone");
            }
        });
        using var projector = new HistoryProjector(source, this.h.Store, this.h.Generations, this.h.Gate, this.h.Monitor, this.h.Status, NullLogger<HistoryProjector>.Instance);
        this.h.Projector.Dispose();
        await projector.InitializeAsync(CancellationToken.None);

        await projector.RebuildAsync(CancellationToken.None);
        await projector.ApplyPendingAsync(CancellationToken.None);

        Assert.True(purged);
        using var reader = projector.Current!.OpenReader();
        Assert.DoesNotContain(reader.ReadTerms(), t => t.StartsWith("zqpurgeme", StringComparison.Ordinal));
        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        Assert.Equal((await snapshot.ReadMetaAsync(CancellationToken.None)).PurgeGeneration, projector.Current!.ReadState().AppliedPurgeGeneration);
    }

    [Fact]
    public async Task RestoredOlderDatabase_RebuildsAndServesNothingStale()
    {
        await this.h.SayAsync("before backup", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        var db = Path.Combine(this.h.Root, "messages.db");
        await this.h.Store.CheckpointWalAsync(CancellationToken.None);
        byte[] backup;
        await using (var live = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var copy = new MemoryStream())
        {
            await live.CopyToAsync(copy);
            backup = copy.ToArray();
        }

        await this.h.SayAsync("zqafterbackup", 1);
        await this.h.Store.DeleteChannelMessagesOlderThanAsync("ch", HistoryHarness.T0.AddSeconds(30));
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        // Restore: the store is closed and the older file put back.
        this.h.Projector.Dispose();
        await this.h.Store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(db + "-wal");
        File.Delete(db + "-shm");
        await File.WriteAllBytesAsync(db, backup);

        await using var restored = new MessageStore(db, NullLogger<MessageStore>.Instance);
        using var projector = new HistoryProjector(restored, restored, this.h.Generations, new HistoryAccessGate(), this.h.Monitor, new HistoryStatus(), NullLogger<HistoryProjector>.Instance);
        await projector.InitializeAsync(CancellationToken.None);

        Assert.True(projector.NeedsRebuild);
        Assert.Null(projector.Current);
        await projector.RebuildAsync(CancellationToken.None);
        using var reader = projector.Current!.OpenReader();
        Assert.Contains("backup", reader.ReadTerms());
        Assert.DoesNotContain("zqafterbackup", reader.ReadTerms());
    }

    [Fact]
    public async Task IncrementalWithEventsAndPurges_EqualsFullRebuild()
    {
        var random = new Random(20261121);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        var ids = new List<long>();
        var words = new[] { "alpha", "beta", "gamma", "delta", "oslo", "bergen" };

        for (var step = 0; step < 120; step++)
        {
            var channel = random.Next(3) switch { 0 => "a", 1 => "b", _ => "c" };
            switch (random.Next(6))
            {
                case 0 when ids.Count > 0:
                    await this.h.Store.UpdateContentAsync(ids[random.Next(ids.Count)], $"{words[random.Next(words.Length)]} corrected");
                    break;
                case 1 when ids.Count > 0:
                    await this.h.Store.UpdateToolCallsAsync(ids[random.Next(ids.Count)], HistoryRows.ToolCalls("event_get"));
                    break;
                case 2:
                    await this.h.Store.BulkInsertAsync([HistoryRows.Row(0, HistoryHarness.T0.AddMinutes(random.Next(600)), words[random.Next(words.Length)], channel: channel)]);
                    break;
                default:
                    ids.Add(await this.h.SayAsync($"{words[random.Next(words.Length)]} {words[random.Next(words.Length)]}", random.Next(600), channel: channel, role: random.Next(2) == 0 ? "user" : "assistant"));
                    break;
            }

            if (step % 15 == 14)
            {
                // Purges by time leave later correction events behind unless the orphan rule removes them.
                await this.h.Store.DeleteChannelMessagesOlderThanAsync(channel, HistoryHarness.T0.AddMinutes(random.Next(600)));
                ids.Clear();
                ids.AddRange((await this.h.Store.GetAllMessagesAsync()).Select(m => m.Id));
            }

            if (step % 7 == 0)
            {
                await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
            }
        }

        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        var incremental = IntegrityAssertions.Snapshot(this.h.Projector.Current!);
        var rebuilt = await IntegrityAssertions.RebuildSnapshotAsync(this.h.Store, this.h.Monitor, Path.Combine(this.h.Root, "rebuild"));
        Assert.Equal(rebuilt.Segments, incremental.Segments);
        Assert.Equal(rebuilt.Postings, incremental.Postings);
        await IntegrityAssertions.AssertEventLogConsistentAsync(this.h.Store);
    }

    /// <summary>Runs a hook right after each snapshot is opened (the snapshot is already pinned).</summary>
    private sealed class SnapshotHookSource(MessageStore inner, Func<Task> afterOpen) : IHistorySource
    {
        public async Task<HistorySourceSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken)
        {
            var snapshot = await inner.OpenSnapshotAsync(cancellationToken);
            await afterOpen();
            return snapshot;
        }

        public Task PruneJournalAsync(long throughSeq, CancellationToken cancellationToken) => inner.PruneJournalAsync(throughSeq, cancellationToken);

        public Task<long> ReadPurgeGenerationAsync(CancellationToken cancellationToken) => inner.ReadPurgeGenerationAsync(cancellationToken);
    }
}
