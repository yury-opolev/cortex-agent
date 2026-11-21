using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>Purges delete events in the same transaction, bump the purge generation and leave a tombstone.</summary>
public sealed class PurgeEventTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"purge-events-{Guid.NewGuid():N}");
    private readonly MessageStore store;

    public PurgeEventTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<List<EventRecord>> AllEventsAsync()
    {
        var events = new List<EventRecord>();
        await using var connection = await this.store.OpenReaderAsync(CancellationToken.None);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Seq, Id, Ts, Type, AuthorKind, AuthorId, ChannelId, RunId, CausationId, MessageRowId, OperationId, Payload FROM Events ORDER BY Seq;";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(HistorySourceSnapshot.ReadEvent(reader));
        }

        return events;
    }

    private Task<string> ToolResultAsync(string channel, string spill) =>
        this.store.AppendAsync(
            new EventDraft(EventTypes.ToolResult, AuthorKinds.Main)
            {
                ChannelId = channel,
                Ts = T0,
                Payload = new JsonObject { ["tool"] = "file_read", ["spill"] = spill },
            },
            CancellationToken.None);

    [Fact]
    public async Task PurgeAsync_Channel_DeletesItsEventsAndOrphansAndWritesTombstone()
    {
        var gone = await this.store.SaveMessageAsync("u", "secret", "assistant", "full", T0);
        await this.store.UpdateContentAsync(gone, "fu…");
        await this.ToolResultAsync("secret", "20261121-a.txt");
        await this.store.SaveMessageAsync("u", "other", "user", "keep", T0);

        var outcome = await this.store.PurgeAsync(
            new MessagePurgeRequest(new HistoryPurgeScope("secret", null)) { PendingSteps = [ErasureSteps.ToolOutput] },
            CancellationToken.None);

        var events = await this.AllEventsAsync();
        Assert.DoesNotContain(events, e => e.Type != EventTypes.HistoryPurged && e.ChannelId == "secret");
        Assert.Single(events, e => e.ChannelId == "other");
        var tombstone = Assert.Single(events, e => e.Type == EventTypes.HistoryPurged);
        Assert.Equal(1, (long?)tombstone.Payload["purgeGeneration"]);
        Assert.Equal(3, outcome.DeletedEvents);
        Assert.Equal(1, outcome.PurgeGeneration);
        var task = Assert.Single(await this.store.ReadPendingErasureAsync(CancellationToken.None));
        Assert.Equal(["20261121-a.txt"], task.SpillFiles);
    }

    [Fact]
    public async Task PurgeAsync_BumpsPurgeGenerationAndJournalsIt()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "a", T0);

        await this.store.DeleteAllMessagesAsync();
        await this.store.DeleteAllMessagesAsync();

        await using var snapshot = await this.store.OpenSnapshotAsync(CancellationToken.None);
        var meta = await snapshot.ReadMetaAsync(CancellationToken.None);
        var purges = (await snapshot.ReadJournalAsync(0, 100, CancellationToken.None)).Where(e => e.Op == HistoryJournalOp.Purge).ToList();
        Assert.Equal(2, meta.PurgeGeneration);
        Assert.Equal([1L, 2L], purges.Select(p => p.PurgeGeneration!.Value));
    }

    [Fact]
    public async Task PurgeAsync_OlderThan_KeepsNewerEventsAndTombstones()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "old", T0);
        var recent = await this.store.SaveMessageAsync("u", "ch", "user", "new", T0.AddDays(2));
        await this.store.DeleteAllMessagesAsync();
        await this.store.SaveMessageAsync("u", "ch", "user", "again", T0.AddDays(3));

        await this.store.DeleteMessagesOlderThanAsync(T0.AddDays(10));

        var events = await this.AllEventsAsync();
        Assert.Equal(2, events.Count(e => e.Type == EventTypes.HistoryPurged));
        Assert.DoesNotContain(events, e => e.MessageRowId == recent);
    }

    [Fact]
    public async Task PurgeAsync_OlderThan_DeletesLaterCorrectionEventsOfPurgedRows()
    {
        // The correction is recorded now, long after the row's timestamp: only the orphan rule catches it.
        var longAgo = new DateTimeOffset(2020, 1, 1, 8, 0, 0, TimeSpan.Zero);
        var old = await this.store.SaveMessageAsync("u", "ch", "assistant", "old reply", longAgo);
        await this.store.UpdateContentAsync(old, "old…");

        await this.store.DeleteChannelMessagesOlderThanAsync("ch", longAgo.AddDays(1));

        Assert.DoesNotContain(await this.AllEventsAsync(), e => e.MessageRowId == old);
    }

    [Fact]
    public async Task PurgeAsync_WithReplacement_SwapsEventsAtomically()
    {
        await this.store.SaveMessageAsync("u", "ch", "user", "old", T0);
        var replacement = new List<MessageRecord>
        {
            new() { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = T0.AddDays(-30) },
        };

        await this.store.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All) { Replacement = replacement }, CancellationToken.None);

        var events = await this.AllEventsAsync();
        Assert.Equal([EventTypes.HistoryPurged, EventTypes.LegacyImport], events.Select(e => e.Type));
        Assert.Equal(1, (long?)events[0].Payload["replacementRows"]);
    }

    [Fact]
    public async Task PurgeAsync_WithReplacement_FailureInReplacementRollsBackEverything()
    {
        var kept = await this.store.SaveMessageAsync("u", "ch", "user", "old", T0);
        var replacement = new List<MessageRecord>
        {
            new() { UserId = "u", ChannelId = "ch", Role = "user", Content = "imported", Timestamp = T0 },
        };
        this.store.FaultInjector = stage =>
        {
            if (stage == "after-row-write")
            {
                throw new InvalidOperationException(stage);
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            this.store.PurgeAsync(new MessagePurgeRequest(HistoryPurgeScope.All) { Replacement = replacement }, CancellationToken.None));

        this.store.FaultInjector = null;
        var events = await this.AllEventsAsync();
        Assert.Equal([kept], (await this.store.GetAllMessagesAsync()).Select(m => m.Id));
        Assert.Equal([EventTypes.MessageReceived], events.Select(e => e.Type));
    }
}
