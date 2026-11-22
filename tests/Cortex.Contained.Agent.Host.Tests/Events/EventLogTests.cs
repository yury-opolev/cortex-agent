using System.Text.Json.Nodes;
using Cortex.Contained.Agent.Host.Events;
using Cortex.Contained.Agent.Host.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cortex.Contained.Agent.Host.Tests.Events;

public sealed class EventLogTests : IAsyncDisposable
{
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), $"eventlog-{Guid.NewGuid():N}");
    private readonly MessageStore store;

    public EventLogTests()
    {
        this.store = new MessageStore(Path.Combine(this.tempDir, "messages.db"), NullLogger<MessageStore>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await this.store.DisposeAsync();
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AppendAsync_ThenGetAsync_RoundTripsEveryColumn()
    {
        var ts = new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);
        var id = await this.store.AppendAsync(
            new EventDraft(EventTypes.ToolCall, AuthorKinds.ScheduledTask)
            {
                AuthorId = "task1",
                ChannelId = "ch",
                RunId = "run_1",
                CausationId = "ev_cause",
                MessageRowId = 7,
                OperationId = "op_1",
                Ts = ts,
                Payload = new JsonObject { ["tool"] = "date_time" },
            },
            CancellationToken.None);

        var stored = await this.store.GetAsync(id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.True(EventIds.IsEventId(stored.Id));
        Assert.Equal(EventTypes.ToolCall, stored.Type);
        Assert.Equal(AuthorKinds.ScheduledTask, stored.AuthorKind);
        Assert.Equal("task1", stored.AuthorId);
        Assert.Equal("ch", stored.ChannelId);
        Assert.Equal("run_1", stored.RunId);
        Assert.Equal("ev_cause", stored.CausationId);
        Assert.Equal(7, stored.MessageRowId);
        Assert.Equal("op_1", stored.OperationId);
        Assert.Equal(ts, stored.Ts);
        Assert.Equal("date_time", (string?)stored.Payload["tool"]);
    }

    [Fact]
    public async Task AppendAsync_SameIdempotencyKey_ReturnsExistingId()
    {
        var draft = new EventDraft(EventTypes.LegacyImport, AuthorKinds.Legacy) { IdempotencyKey = "legacy:1" };

        var first = await this.store.AppendAsync(draft, CancellationToken.None);
        var second = await this.store.AppendAsync(draft, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ListRunAndOperation_ReturnOnlyMatchingEventsOldestFirst()
    {
        var a = await this.store.AppendAsync(new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { RunId = "run_a", OperationId = "op_x" }, CancellationToken.None);
        await this.store.AppendAsync(new EventDraft(EventTypes.ToolCall, AuthorKinds.Main) { RunId = "run_b" }, CancellationToken.None);
        var c = await this.store.AppendAsync(new EventDraft(EventTypes.ToolResult, AuthorKinds.Main) { RunId = "run_a" }, CancellationToken.None);

        Assert.Equal([a, c], (await this.store.ListRunAsync("run_a", CancellationToken.None)).Select(e => e.Id));
        Assert.Equal([a], (await this.store.ListOperationAsync("op_x", CancellationToken.None)).Select(e => e.Id));
    }

    [Fact]
    public async Task AppendAsync_PayloadOver8KB_Throws()
    {
        var draft = new EventDraft(EventTypes.ToolResult, AuthorKinds.Main) { Payload = new JsonObject { ["digest"] = new string('x', 9000) } };

        await Assert.ThrowsAsync<InvalidOperationException>(() => this.store.AppendAsync(draft, CancellationToken.None));
    }
}
