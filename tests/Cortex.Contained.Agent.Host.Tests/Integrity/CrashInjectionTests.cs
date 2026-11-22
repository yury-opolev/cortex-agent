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

    // The mutations that pass through each fault-injection stage; a crash there must throw.
    private static readonly Dictionary<string, string[]> Reached = new()
    {
        ["after-row-write"] = ["save", "update-content", "update-toolcalls", "bulk", "import"],
        ["after-event-write"] = ["save", "update-content", "update-toolcalls", "bulk", "purge-channel", "import"],
        ["after-purge-delete"] = ["purge-channel", "import"],
    };

    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var stage in Reached.Keys)
        {
            foreach (var mutation in new[] { "save", "update-content", "update-toolcalls", "bulk", "purge-channel", "import" })
            {
                data.Add(stage, mutation, Reached[stage].Contains(mutation));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Mutation_CrashAtStage_RollsBackAsOneUnit(string stage, string mutation, bool reachesStage)
    {
        var existing = await this.h.SayAsync("existing row", 0);
        await this.h.SayAsync("other channel", 1, channel: "other");
        var before = await this.h.Store.GetAllMessagesAsync();
        this.h.Store.FaultInjector = s => { if (s == stage) { throw new IOException("crash at " + s); } };

        Task Run() => mutation switch
        {
            "save" => this.h.SayAsync("new row", 2),
            "update-content" => this.h.Store.UpdateContentAsync(existing, "changed"),
            "update-toolcalls" => this.h.Store.UpdateToolCallsAsync(existing, "[]"),
            "bulk" => this.h.Store.BulkInsertAsync([HistoryRows.Row(0, HistoryHarness.T0, "bulk")]),
            "purge-channel" => this.h.Store.DeleteChannelMessagesAsync("other"),
            _ => this.h.Store.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All) { Replacement = [HistoryRows.Row(0, HistoryHarness.T0, "imported")] }, CancellationToken.None),
        };

        if (reachesStage)
        {
            await Assert.ThrowsAsync<IOException>(Run);
            this.h.Store.FaultInjector = null;
            Assert.Equal(before, await this.h.Store.GetAllMessagesAsync());
        }
        else
        {
            await Run();
        }

        this.h.Store.FaultInjector = null;
        await IntegrityAssertions.AssertEventLogConsistentAsync(this.h.Store);
    }
}

