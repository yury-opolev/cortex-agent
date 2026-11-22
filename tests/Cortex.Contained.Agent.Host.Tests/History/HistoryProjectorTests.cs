using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

public sealed class HistoryProjectorTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private IReadOnlyList<IndexedSegment> Segments()
    {
        using var reader = this.h.Projector.Current!.OpenReader();
        return reader.ReadSegments();
    }

    [Fact]
    public async Task InitializeAndRebuild_EmptyDirectory_BuildsCompleteCurrentGeneration()
    {
        await this.h.SayAsync("hello oslo", 0);

        await this.h.Projector.InitializeAsync(CancellationToken.None);
        Assert.True(this.h.Projector.NeedsRebuild);
        await this.h.CatchUpAsync();

        Assert.Equal(1, this.h.Generations.ReadCurrent());
        Assert.True(this.h.Projector.Current!.ReadState().BuildComplete);
        Assert.Single(this.Segments());
        Assert.False(this.h.Status.Snapshot().BuildInProgress);
    }

    [Fact]
    public async Task ApplyPendingAsync_EveryMutation_RecomputesChannel()
    {
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        var id = await this.h.SayAsync("meet at ten", 0);
        await this.h.SayAsync("other", 0, channel: "b");
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(2, this.Segments().Count);

        await this.h.Store.UpdateContentAsync(id, "meet at elf");
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
        using (var reader = this.h.Projector.Current!.OpenReader())
        {
            Assert.Equal(0, reader.DocumentFrequency("ten"));
            Assert.Equal(1, reader.DocumentFrequency("elf"));
        }

        await this.h.Store.DeleteChannelMessagesAsync("ch");
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
        Assert.Equal(["b"], this.Segments().Select(s => s.ChannelId));
    }

    [Fact]
    public async Task ApplyPendingAsync_PrunesJournalUpToCheckpoint()
    {
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.h.SayAsync("a", 0);
        await this.h.SayAsync("b", 1);

        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        await using var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None);
        var meta = await snapshot.ReadMetaAsync(CancellationToken.None);
        Assert.Equal(meta.JournalHead, meta.JournalPrunedThrough);
        Assert.Equal(meta.JournalHead, this.h.Projector.Current!.ReadState().CheckpointJournalSeq);
    }

    [Fact]
    public async Task Restart_AfterIndexAndPrune_ResumesWithoutRebuild()
    {
        await this.h.SayAsync("a", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.h.SayAsync("b", 1);
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        var restarted = this.h.NewProjector();
        await restarted.InitializeAsync(CancellationToken.None);

        Assert.False(restarted.NeedsRebuild);
        Assert.Equal(1, restarted.Current!.ReadState().Generation);
    }

    [Fact]
    public async Task Restart_CheckpointBelowPrunedThrough_Rebuilds()
    {
        await this.h.SayAsync("a", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.h.SayAsync("b", 1);
        await using (var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None))
        {
            var head = (await snapshot.ReadMetaAsync(CancellationToken.None)).JournalHead;
            await this.h.Store.PruneJournalAsync(head, CancellationToken.None);
        }

        var restarted = this.h.NewProjector();
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.True(restarted.NeedsRebuild);
        await this.h.CatchUpAsync();

        Assert.Equal(2, this.h.Generations.ReadCurrent());
        Assert.Equal([2L], this.h.Generations.List());
        Assert.Single(this.Segments());
    }

    [Fact]
    public async Task ApplyPendingAsync_JournalPrunedPastCheckpointWhileRunning_RequiresRebuild()
    {
        await this.h.SayAsync("a", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.h.SayAsync("b", 1);
        await using (var snapshot = await this.h.Store.OpenSnapshotAsync(CancellationToken.None))
        {
            var head = (await snapshot.ReadMetaAsync(CancellationToken.None)).JournalHead;
            await this.h.Store.PruneJournalAsync(head, CancellationToken.None);
        }

        Assert.Equal(ProjectorApplyResult.RebuildRequired, await this.h.Projector.ApplyPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ApplyPendingAsync_RestoredSource_RequiresRebuildAndRetiresGeneration()
    {
        await this.h.SayAsync("old", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        var replacement = new MessageRecord { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = HistoryHarness.T0 };
        await this.h.Store.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All) { Replacement = [replacement] }, CancellationToken.None);

        Assert.Equal(ProjectorApplyResult.RebuildRequired, await this.h.Projector.ApplyPendingAsync(CancellationToken.None));
        await this.h.Projector.RetireCurrentAsync(writeLeaseHeld: false, CancellationToken.None);
        Assert.Null(this.h.Projector.Current);
        Assert.Empty(this.h.Generations.List());

        await this.h.CatchUpAsync();
        using var reader = this.h.Projector.Current!.OpenReader();
        Assert.Equal(1, reader.DocumentFrequency("imported"));
        Assert.Equal(0, reader.DocumentFrequency("old"));
    }

    [Theory]
    [InlineData(CutOverStep.BeforeComplete)]
    [InlineData(CutOverStep.AfterComplete)]
    [InlineData(CutOverStep.AfterPointer)]
    public async Task Rebuild_CrashAtCutOverStep_RestartYieldsConsistentCurrentGeneration(CutOverStep crashAt)
    {
        await this.h.SayAsync("first", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        this.h.Options = new HistoryOptions { Enabled = true, TimeZone = "UTC", IdleGapMinutes = 5 };
        await this.h.SayAsync("second", 1);

        var crashing = this.h.NewProjector();
        await crashing.InitializeAsync(CancellationToken.None);
        crashing.CutOverHook = step => { if (step == crashAt) { throw new IOException("crash"); } };
        await Assert.ThrowsAsync<IOException>(() => crashing.RebuildAsync(CancellationToken.None));

        var restarted = this.h.NewProjector();
        await restarted.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        var current = restarted.Current!;
        Assert.True(current.ReadState().BuildComplete);
        Assert.Equal(this.h.Generations.ReadCurrent(), current.ReadState().Generation);
        Assert.Equal([current.ReadState().Generation], this.h.Generations.List());
        using var reader = current.OpenReader();
        Assert.Equal(1, reader.DocumentFrequency("second"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Initialize_GenerationFileWithoutState_IsRetiredAndRebuilt(bool emptyFile)
    {
        await this.h.SayAsync("survivor", 0);
        var partial = this.h.Generations.PathFor(1);
        if (emptyFile)
        {
            await File.WriteAllBytesAsync(partial, []);
        }
        else
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={partial};Pooling=False");
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE segments (SegId INTEGER PRIMARY KEY);";
            await cmd.ExecuteNonQueryAsync();
        }

        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        Assert.Single(this.Segments());
        Assert.True(this.h.Projector.Current!.ReadState().BuildComplete);
        Assert.Equal([this.h.Projector.Current.ReadState().Generation], this.h.Generations.List());
    }

    [Fact]
    public async Task Initialize_LeftoverCreateTempFiles_AreRemovedAndRebuildSucceeds()
    {
        await this.h.SayAsync("survivor", 0);
        var temp = this.h.Generations.PathFor(1) + ".creating";
        await File.WriteAllTextAsync(temp, "half-written");
        await File.WriteAllTextAsync(temp + "-wal", "half-written");

        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        Assert.Empty(Directory.EnumerateFiles(this.h.Generations.Directory, "*.creating*"));
        Assert.Single(this.Segments());
    }

    [Fact]
    public async Task EraseAsync_DuringInProgressBuild_DeletesBuildFile()
    {
        await this.h.SayAsync("secret", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        this.h.Projector.CutOverHook = _ => throw new IOException("stop before cut-over");
        await Assert.ThrowsAsync<IOException>(() => this.h.Projector.RebuildAsync(CancellationToken.None));
        Assert.Single(this.h.Generations.List());

        await using (await this.h.Gate.AcquireWriteAsync(CancellationToken.None))
        {
            Assert.True(await this.h.Projector.EraseAsync(CancellationToken.None));
        }

        Assert.Empty(this.h.Generations.List());
    }

    [Fact]
    public async Task EraseAsync_FeatureDisabled_DeletesAllGenerations()
    {
        await this.h.SayAsync("secret", 0);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        this.h.Options = new HistoryOptions { Enabled = false, TimeZone = "UTC" };

        await using (await this.h.Gate.AcquireWriteAsync(CancellationToken.None))
        {
            Assert.True(await this.h.Projector.EraseAsync(CancellationToken.None));
        }

        Assert.Empty(this.h.Generations.List());
        Assert.Null(this.h.Projector.Current);
    }

    [Fact]
    public async Task IncrementalApplication_RandomMutations_EqualsFullRebuild()
    {
        var random = new Random(20261121);
        string[] vocabulary = ["oslo", "tog", "søndag", "поезд", "ёлка", "meeting", "budget", "kaffe", "йогурт", "plan"];
        string[] channels = ["a", "b", "c"];
        string Text() => string.Join(' ', Enumerable.Range(0, random.Next(1, 8)).Select(_ => vocabulary[random.Next(vocabulary.Length)]));
        var ids = new List<long>();

        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();

        for (var op = 0; op < 300; op++)
        {
            var minute = random.Next(0, 3000);
            var channel = channels[random.Next(channels.Length)];
            switch (random.Next(10))
            {
                case 0 or 1 or 2:
                    ids.Add(await this.h.SayAsync(Text(), minute, channel, random.Next(2) == 0 ? "user" : "assistant",
                        (MessageCategory)random.Next(0, 4)));
                    break;
                case 3 when ids.Count > 0:
                    var target = ids[random.Next(ids.Count)];
                    await this.h.Store.UpdateContentAsync(target, "edited " + vocabulary[random.Next(vocabulary.Length)]);
                    break;
                case 4 when ids.Count > 0:
                    await this.h.Store.UpdateToolCallsAsync(ids[random.Next(ids.Count)], HistoryRows.ToolCalls(random.Next(2) == 0 ? "history_search" : "memory_search"));
                    break;
                case 5:
                    await this.h.Store.BulkInsertAsync(
                    [
                        new MessageRecord { UserId = "u", ChannelId = channel, Role = "user", Content = Text(), Timestamp = HistoryHarness.T0.AddMinutes(minute - 500) },
                        new MessageRecord { UserId = "u", ChannelId = channels[random.Next(channels.Length)], Role = "assistant", Content = Text(), Timestamp = HistoryHarness.T0.AddMinutes(minute) },
                    ]);
                    break;
                case 6:
                    await this.h.Store.DeleteChannelMessagesOlderThanAsync(channel, HistoryHarness.T0.AddMinutes(random.Next(0, 600)));
                    break;
                case 7:
                    await this.h.Store.DeleteMessagesOlderThanAsync(HistoryHarness.T0.AddMinutes(random.Next(0, 300)));
                    break;
                case 8 when op % 97 == 0:
                    await this.h.Store.DeleteChannelMessagesAsync(channel);
                    break;
                case 9 when op == 150:
                    await this.h.Store.DeleteAllMessagesAsync();
                    break;
            }

            if (op % 13 == 0)
            {
                await this.h.Projector.ApplyPendingAsync(CancellationToken.None);
            }
        }

        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        var referenceGenerations = new HistoryIndexGenerations(Path.Combine(this.h.Root, "reference"));
        using var reference = new HistoryProjector(this.h.Store, this.h.Store, referenceGenerations, new HistoryAccessGate(), this.h.Monitor, new HistoryStatus(), NullLogger<HistoryProjector>.Instance);
        await reference.InitializeAsync(CancellationToken.None);
        await reference.RebuildAsync(CancellationToken.None);

        using var incremental = this.h.Projector.Current!.OpenReader();
        using var rebuilt = reference.Current!.OpenReader();
        Assert.NotEmpty(rebuilt.ReadSegments());
        Assert.Equal(Comparable(rebuilt.ReadSegments()), Comparable(incremental.ReadSegments()));
        Assert.Equal(rebuilt.ReadTerms(), incremental.ReadTerms());
        Assert.Equal(rebuilt.ReadPostings(), incremental.ReadPostings());

        static IEnumerable<string> Comparable(IEnumerable<IndexedSegment> segments) =>
            segments.Select(s => $"{s.ChannelId}|{s.StartTs}|{s.EndTs}|{s.LocalDate}|{string.Join(',', s.MsgIds)}|{s.HasInternal}|{s.HasRecallEcho}|{s.Fingerprint}");
    }
}
