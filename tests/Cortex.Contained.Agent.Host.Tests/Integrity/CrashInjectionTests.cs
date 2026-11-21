using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Agent.Host.Tests.History;

namespace Cortex.Contained.Agent.Host.Tests.Integrity;

/// <summary>Crash injection at every write stage of every mutation path leaves rows, journal and events consistent.</summary>
[Trait("Category", "Integrity")]
public sealed class CrashInjectionTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var stage in new[] { "after-row-write", "after-event-write", "after-purge-delete" })
        {
            foreach (var mutation in new[] { "save", "update-content", "update-toolcalls", "bulk", "purge-channel", "import" })
            {
                data.Add(stage, mutation);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Mutation_CrashAtStage_RollsBackAsOneUnit(string stage, string mutation)
    {
        var existing = await this.h.SayAsync("existing row", 0);
        await this.h.SayAsync("other channel", 1, channel: "other");
        var before = await this.h.Store.GetAllMessagesAsync();
        this.h.Store.FaultInjector = s => { if (s == stage) { throw new IOException("crash at " + s); } };

        try
        {
            await (mutation switch
            {
                "save" => this.h.SayAsync("new row", 2),
                "update-content" => this.h.Store.UpdateContentAsync(existing, "changed"),
                "update-toolcalls" => this.h.Store.UpdateToolCallsAsync(existing, "[]"),
                "bulk" => this.h.Store.BulkInsertAsync([HistoryRows.Row(0, HistoryHarness.T0, "bulk")]),
                "purge-channel" => this.h.Store.DeleteChannelMessagesAsync("other"),
                _ => this.h.Store.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All) { Replacement = [HistoryRows.Row(0, HistoryHarness.T0, "imported")] }, CancellationToken.None),
            });
        }
        catch (IOException)
        {
            this.h.Store.FaultInjector = null;
            Assert.Equal(before, await this.h.Store.GetAllMessagesAsync());
        }

        this.h.Store.FaultInjector = null;
        await IntegrityAssertions.AssertEventLogConsistentAsync(this.h.Store);
    }
}
