using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

/// <summary>One writer boundary (§4.1): every Messages mutation writes its event in the same transaction.</summary>
public sealed class MessageStoreEventTests : IAsyncDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"msgstore-events-{Guid.NewGuid():N}");
    private readonly MessageStore store;

    public MessageStoreEventTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<IReadOnlyList<EventRecord>> EventsOfAsync(long rowId)
    {
        await using var snapshot = await this.store.OpenSnapshotAsync(CancellationToken.None);
        return await snapshot.ReadMessageEventsAsync(rowId, CancellationToken.None);
    }

    [Theory]
    [InlineData("user", MessageCategory.Normal, EventTypes.MessageReceived, AuthorKinds.User)]
    [InlineData("assistant", MessageCategory.Normal, EventTypes.MessageSent, AuthorKinds.Main)]
    [InlineData("assistant", MessageCategory.Proactive, EventTypes.MessageSent, AuthorKinds.Main)]
    [InlineData("user", MessageCategory.Internal, EventTypes.InternalNote, AuthorKinds.Main)]
    [InlineData("assistant", MessageCategory.System, EventTypes.SystemNote, AuthorKinds.System)]
    public async Task SaveMessageAsync_WritesTypedContentFreeEvent(string role, MessageCategory category, string type, string author)
    {
        var id = await this.store.SaveMessageAsync("u", "ch", role, "zqsecret text", T0, category: category);

        var ev = Assert.Single(await this.EventsOfAsync(id));
        Assert.Equal(type, ev.Type);
        Assert.Equal(author, ev.AuthorKind);
        Assert.Equal("ch", ev.ChannelId);
        Assert.Equal(T0, ev.Ts);
        Assert.Equal(EventHashing.Sha256Hex("zqsecret text"), (string?)ev.Payload["sha256"]);
        Assert.DoesNotContain("zqsecret", ev.Payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveMessageAsync_InsideRunScope_CarriesRunIdentity()
    {
        var run = new RunProvenance("run_x", AuthorKinds.ScheduledTask, "t1", "ch", "scheduled task t1");
        long id;
        using (RunScope.Begin(run))
        {
            id = await this.store.SaveMessageAsync("assistant", "ch", "assistant", "done", T0);
        }

        var ev = Assert.Single(await this.EventsOfAsync(id));
        Assert.Equal("run_x", ev.RunId);
        Assert.Equal(AuthorKinds.ScheduledTask, ev.AuthorKind);
        Assert.Equal("t1", ev.AuthorId);
    }

    [Fact]
    public async Task UpdateContentAsync_WritesCorrectionThatSupersedesOriginal()
    {
        var id = await this.store.SaveMessageAsync("assistant", "ch", "assistant", "the full answer with an unspoken tail", T0);

        await this.store.UpdateContentAsync(id, "the full answer…");

        var events = await this.EventsOfAsync(id);
        Assert.Equal(2, events.Count);
        var correction = events[1];
        Assert.Equal(EventTypes.MessageCorrected, correction.Type);
        Assert.Equal(events[0].Id, correction.CausationId);
        Assert.Equal(EventHashing.Sha256Hex("the full answer…"), (string?)correction.Payload["sha256"]);
        Assert.DoesNotContain("unspoken", correction.Payload.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateToolCallsAsync_WritesMessageUpdated()
    {
        var id = await this.store.SaveMessageAsync("assistant", "ch", "assistant", "x", T0);

        await this.store.UpdateToolCallsAsync(id, "[]");

        Assert.Equal(EventTypes.MessageUpdated, (await this.EventsOfAsync(id))[^1].Type);
    }

    [Fact]
    public async Task BulkInsertAsync_WritesLegacyImportPerRow()
    {
        var inserted = await this.store.BulkInsertAsync(
            [
                new MessageRecord { UserId = "u", ChannelId = "a", Role = "user", Content = "1", Timestamp = T0 },
                new MessageRecord { UserId = "u", ChannelId = "b", Role = "assistant", Content = "2", Timestamp = T0 },
            ]);

        var all = await this.store.GetAllMessagesAsync();
        Assert.Equal(2, inserted);
        foreach (var row in all)
        {
            var ev = Assert.Single(await this.EventsOfAsync(row.Id));
            Assert.Equal(EventTypes.LegacyImport, ev.Type);
            Assert.Equal(AuthorKinds.Legacy, ev.AuthorKind);
        }
    }

    [Fact]
    public async Task SaveMessageAsync_FailureAfterEventWrite_RollsBackRowJournalAndEvent()
    {
        this.store.FaultInjector = stage => { if (stage == "after-event-write") { throw new InvalidOperationException(stage); } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => this.store.SaveMessageAsync("u", "ch", "user", "x", T0));

        this.store.FaultInjector = null;
        Assert.Equal(0, await this.store.GetTotalMessageCountAsync());
        await using var snapshot = await this.store.OpenSnapshotAsync(CancellationToken.None);
        Assert.Empty(await snapshot.ReadJournalAsync(0, 10, CancellationToken.None));
        Assert.Empty(await snapshot.ReadMessageEventsAsync(1, CancellationToken.None));
    }
}
