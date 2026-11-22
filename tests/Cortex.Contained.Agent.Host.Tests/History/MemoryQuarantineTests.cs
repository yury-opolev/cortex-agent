using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Memory;
using Cortex.Contained.Agent.Host.Storage;
using MemoryMcp.Core.Models;
using MemoryMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>D10: memories created in a purged window are hidden until the owner reviews them.</summary>
public sealed class MemoryQuarantineTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"quarantine-{Guid.NewGuid():N}");
    private readonly MessageStore store;
    private readonly IMemoryCreationTimes times = Substitute.For<IMemoryCreationTimes>();
    private readonly MemoryQuarantine quarantine;

    public MemoryQuarantineTests()
    {
        this.store = new MessageStore(Path.Combine(this.dir, "messages.db"), NullLogger<MessageStore>.Instance);
        this.quarantine = new MemoryQuarantine(this.store, this.times, TimeProvider.System);
        this.times.ListCreationTimesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new MemoryCreationTime("during", T0.AddHours(2)),
            new MemoryCreationTime("slack", T0.AddHours(20)),
            new MemoryCreationTime("before", T0.AddDays(-3)),
            new MemoryCreationTime("after", T0.AddDays(3)),
        ]);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task PurgeOneConversationAsync()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.SaveMessageAsync("u", "ch", "user", "b", T0.AddHours(1));
        await this.store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("ch", null)) { Now = T0.AddDays(2) }, CancellationToken.None);
    }

    [Fact]
    public async Task GetQuarantinedIdsAsync_NoPurges_IsEmptyWithoutListingMemories()
    {
        Assert.Empty(await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
        await this.times.DidNotReceive().ListCreationTimesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetQuarantinedIdsAsync_AfterPurge_HidesInWindowMemoriesUntilKept()
    {
        await this.PurgeOneConversationAsync();

        Assert.Equal(["during", "slack"], (await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None)).Order());

        await this.quarantine.KeepAsync("during", CancellationToken.None);
        Assert.Equal(["slack"], await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PurgeWithQuarantineCap_WindowsEndAtTheCap()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.SaveMessageAsync("u", "ch", "user", "b", T0.AddHours(1));

        await this.store.PurgeAsync(
            new MessagePurgeRequest(new HistoryPurgeScope("ch", null)) { Now = T0.AddDays(2), QuarantineCap = T0.AddHours(3) },
            CancellationToken.None);

        Assert.Equal(["during"], await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Decorator_SearchAndFindSimilar_OverFetchAndHideQuarantined()
    {
        await this.PurgeOneConversationAsync();
        var inner = Substitute.For<IMemoryService>();
        var all = new List<SearchResult>
        {
            new() { MemoryId = "during", Content = "x", Score = 0.9f },
            new() { MemoryId = "after", Content = "y", Score = 0.8f },
            new() { MemoryId = "before", Content = "z", Score = 0.7f },
        };
        inner.SearchAsync("q", 4, null, null, Arg.Any<CancellationToken>()).Returns(all);
        inner.FindSimilarAsync("q", 3, 0.3f, Arg.Any<CancellationToken>()).Returns(all);
        var decorated = new QuarantineAwareMemoryService(inner, this.quarantine);

        var searched = await decorated.SearchAsync("q", limit: 2);
        var similar = await decorated.FindSimilarAsync("q", 1, 0.3f);

        Assert.Equal(["after", "before"], searched.Select(r => r.MemoryId));
        Assert.Equal(["after"], similar.Select(r => r.MemoryId));
    }

    [Fact]
    public async Task Decorator_NoQuarantine_PassesLimitThrough()
    {
        var inner = Substitute.For<IMemoryService>();
        inner.SearchAsync("q", 5, null, null, Arg.Any<CancellationToken>()).Returns([]);
        var decorated = new QuarantineAwareMemoryService(inner, this.quarantine);

        await decorated.SearchAsync("q");

        await inner.Received(1).SearchAsync("q", 5, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CountInWindowsAsync_ReportsAffectedMemories()
    {
        var windows = QuarantineWindowBuilder.Build([T0], TimeSpan.FromHours(6), TimeSpan.FromHours(24), T0.AddDays(2));

        Assert.Equal(2, await this.quarantine.CountInWindowsAsync(windows, CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_AfterPurge_ReportsThePurgeAndItsChannel()
    {
        await this.PurgeOneConversationAsync();

        var listed = await this.quarantine.ListAsync(CancellationToken.None);

        Assert.Equal(["slack", "during"], listed.Select(q => q.MemoryId));
        Assert.All(listed, q => Assert.Equal("ch", q.Source.ChannelId));
        Assert.All(listed, q => Assert.Equal(T0.AddDays(2), q.Source.PurgedAt));
    }

    [Fact]
    public async Task InErasureAsync_OnlyThatPurgesWindows()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);
        await this.store.SaveMessageAsync("u", "other", "user", "b", T0.AddDays(3));
        var first = new MessagePurgeRequest(new HistoryPurgeScope("ch", null)) { Now = T0.AddDays(5) };
        await this.store.PurgeAsync(first, CancellationToken.None);
        await this.store.PurgeAsync(new MessagePurgeRequest(new HistoryPurgeScope("other", null)) { Now = T0.AddDays(5) }, CancellationToken.None);

        Assert.Equal(["during", "slack"], (await this.quarantine.InErasureAsync(first.ErasureId, CancellationToken.None)).Order());
        Assert.Equal(3, (await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task KeepManyAsync_KeepsQuarantinedAndSkipsTheRest()
    {
        await this.PurgeOneConversationAsync();

        var (kept, skipped) = await this.quarantine.KeepManyAsync(["during", "slack", "after", "during"], CancellationToken.None);

        Assert.Equal(["during", "slack"], kept);
        Assert.Equal(["after"], skipped);
        Assert.Empty(await this.quarantine.GetQuarantinedIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadQuarantineWindowRecordsAsync_DatabaseFromBeforeChannelColumn_IsMigrated()
    {
        var legacyDir = Path.Combine(Path.GetTempPath(), $"quarantine-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        var path = Path.Combine(legacyDir, "messages.db");
        try
        {
            using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                legacy.Open();
                using var cmd = legacy.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE MemoryQuarantineWindows (Id INTEGER PRIMARY KEY AUTOINCREMENT, ErasureId TEXT NOT NULL, FromTs TEXT NOT NULL, ToTs TEXT NOT NULL, CreatedAt TEXT NOT NULL);
                    INSERT INTO MemoryQuarantineWindows (ErasureId, FromTs, ToTs, CreatedAt) VALUES ('e1', '2026-05-04 08:00:00', '2026-05-05 08:00:00', '2026-05-06 08:00:00');
                    """;
                cmd.ExecuteNonQuery();
            }

            await using var migrated = new MessageStore(path, NullLogger<MessageStore>.Instance);
            var record = Assert.Single(await migrated.ReadQuarantineWindowRecordsAsync(CancellationToken.None));

            Assert.Equal("e1", record.ErasureId);
            Assert.Null(record.ChannelId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(legacyDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
