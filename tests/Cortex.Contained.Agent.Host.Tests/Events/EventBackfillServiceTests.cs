using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>The explicit event backfill (D2): idempotent, never resurrects purged rows.</summary>
public sealed class EventBackfillServiceTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();
    private readonly EventBackfillService backfill;

    public EventBackfillServiceTests()
    {
        this.backfill = new EventBackfillService(this.h.Store, this.h.Gate, TimeProvider.System, NullLogger<EventBackfillService>.Instance);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    /// <summary>Simulates rows written before the event log existed.</summary>
    private async Task ForgetAllEventsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(this.h.Root, "messages.db")};Pooling=False");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM Events;";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> EventCountAsync(string type)
    {
        await using var connection = await this.h.Store.OpenReaderAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Events WHERE Type = @t;";
        cmd.Parameters.AddWithValue("@t", type);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Status_AfterRun_ReportsTheLastRun()
    {
        Assert.Equal(new EventBackfillStatus(false, null, null), this.backfill.Status);
        await this.h.SayAsync("m", 0);
        await this.ForgetAllEventsAsync();

        var report = await this.backfill.RunAsync(CancellationToken.None);

        var status = this.backfill.Status;
        Assert.False(status.Running);
        Assert.Equal(report, status.Last);
        Assert.NotNull(status.LastRunAt);
    }

    [Fact]
    public async Task Status_WhileWaitingForTheMaintenanceWindow_ReportsRunning()
    {
        var lease = await this.h.Gate.AcquireWriteAsync(CancellationToken.None);
        var run = this.backfill.RunAsync(CancellationToken.None);

        Assert.True(this.backfill.Status.Running);
        await lease.DisposeAsync();
        await run;
        Assert.False(this.backfill.Status.Running);
    }

    [Fact]
    public async Task RunAsync_LegacyRows_GetOneLegacyImportEach()
    {
        for (var i = 0; i < 2500; i++)
        {
            await this.h.SayAsync($"m{i}", i % 50);
        }

        await this.ForgetAllEventsAsync();

        var report = await this.backfill.RunAsync(CancellationToken.None);

        Assert.Equal(2500, report.Scanned);
        Assert.Equal(2500, report.Inserted);
        Assert.Equal(2500, await this.EventCountAsync(EventTypes.LegacyImport));
    }

    [Fact]
    public async Task RunAsync_Twice_IsIdempotent()
    {
        await this.h.SayAsync("a", 0);
        await this.ForgetAllEventsAsync();
        await this.h.SayAsync("b", 1);

        var first = await this.backfill.RunAsync(CancellationToken.None);
        var second = await this.backfill.RunAsync(CancellationToken.None);

        Assert.Equal(1, first.Inserted);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, await this.EventCountAsync(EventTypes.LegacyImport));
        Assert.Equal(1, await this.EventCountAsync(EventTypes.MessageReceived));
    }

    [Fact]
    public async Task RunAsync_AfterPurge_NeverResurrectsPurgedRows()
    {
        await this.h.SayAsync("zqkeep", 0, channel: "keep");
        await this.h.SayAsync("zqpurged", 0, channel: "gone");
        await this.ForgetAllEventsAsync();
        await this.h.Store.DeleteChannelMessagesAsync("gone");

        await this.backfill.RunAsync(CancellationToken.None);

        await using var connection = await this.h.Store.OpenReaderAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Events WHERE ChannelId = 'gone' AND Type <> 'history_purged';";
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        Assert.Equal(1, await this.EventCountAsync(EventTypes.LegacyImport));
    }

    [Fact]
    public async Task RunAsync_HoldsTheMaintenanceWindow()
    {
        await this.h.SayAsync("a", 0);
        Task<EventBackfillReport> run;
        await using (await this.h.Gate.AcquireReadAsync(CancellationToken.None))
        {
            run = this.backfill.RunAsync(CancellationToken.None);
            await Task.Delay(100);
            Assert.False(run.IsCompleted);
        }

        await run;
    }
}
