using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.History;

public sealed class HistoryRecallServiceTests : IAsyncDisposable
{
    private readonly HistoryHarness h = new();
    private readonly HistoryRecallService recall;

    public HistoryRecallServiceTests()
    {
        this.recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task IndexAsync()
    {
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
    }

    private Task<HistorySearchResult> SearchAsync(string query, HistoryOrder order = HistoryOrder.Relevance, int limit = 8,
        bool includeInternal = false, bool includeEchoes = false, string? cursor = null) =>
        this.recall.SearchAsync(new HistorySearchRequest
        {
            Query = query, Order = order, Limit = limit, IncludeInternal = includeInternal, IncludeEchoes = includeEchoes, Cursor = cursor,
        }, CancellationToken.None);

    private static IEnumerable<long> Ids(HistorySearchResult result) => result.Hits.SelectMany(h => h.Lines).Select(l => l.MessageId);

    [Fact]
    public async Task SearchAsync_Relevance_ReturnsVerifiedMatchingLinesOnly()
    {
        var hit = await this.h.SayAsync("the night train to Oslo leaves at nine", 0);
        await this.h.SayAsync("sounds good", 1, role: "assistant");
        await this.IndexAsync();

        var result = await this.SearchAsync("oslo");

        Assert.Equal([hit], Ids(result));
        Assert.Equal(HistoryHitSource.Indexed, result.Hits[0].Source);
        Assert.True(result.Coverage.BuildComplete);
        Assert.Equal(["ch"], result.Coverage.Channels.Select(c => c.ChannelId));
    }

    [Fact]
    public async Task SearchAsync_RowDeletedAfterIndexingBeforeProjector_IsNeverReturned()
    {
        await this.h.SayAsync("zqx secret plan", 0);
        await this.IndexAsync();

        await this.h.Store.DeleteAllMessagesAsync();

        var result = await this.SearchAsync("zqx");
        Assert.Empty(result.Hits);
        Assert.Equal(1, result.Unavailable);
    }

    [Fact]
    public async Task SearchAsync_SameLengthEditBeforeProjector_OldTextNeverReturned()
    {
        var id = await this.h.SayAsync("meet at oslo", 0);
        await this.IndexAsync();

        await this.h.Store.UpdateContentAsync(id, "meet at bergs");

        Assert.Empty((await this.SearchAsync("oslo")).Hits);
        var edited = await this.SearchAsync("bergs");
        Assert.Equal([id], Ids(edited));
        Assert.Equal(HistoryHitSource.Unindexed, edited.Hits[0].Source);
    }

    [Fact]
    public async Task SearchAsync_RecentRowsNotYetIndexed_FoundViaUnindexedTail()
    {
        await this.IndexAsync();
        var id = await this.h.SayAsync("brand new kayak", 5);

        var result = await this.SearchAsync("kayak");

        Assert.Equal([id], Ids(result));
        Assert.Equal(HistoryHitSource.Unindexed, result.Hits[0].Source);
        Assert.Equal(1, result.Coverage.UnindexedTail);
        Assert.True(result.Coverage.Lag > 0);
    }

    [Fact]
    public async Task SearchAsync_InternalRows_ExcludedByDefaultAndLabelledWhenIncluded()
    {
        var task = await this.h.SayAsync("reminder: water the ficus", 0, category: MessageCategory.Internal);
        await this.IndexAsync();

        Assert.Empty((await this.SearchAsync("ficus")).Hits);

        var included = await this.SearchAsync("ficus", includeInternal: true);
        var line = Assert.Single(included.Hits.SelectMany(h => h.Lines));
        Assert.Equal(task, line.MessageId);
        Assert.Equal(HistoryLineLabel.Internal, line.Label);
    }

    [Fact]
    public async Task SearchAsync_EchoOnlyHits_ReturnedOnlyWhenNothingElseMatchesOrRequested()
    {
        var echo = await this.h.SayAsync("you said the code is 4711", 0, role: "assistant", toolCalls: HistoryRows.ToolCalls("history_search"));
        await this.IndexAsync();

        var fallback = await this.SearchAsync("4711");
        Assert.Equal([echo], Ids(fallback));
        Assert.True(fallback.Hits[0].EchoOnly);

        var original = await this.h.SayAsync("the code is 4711", 120);
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        Assert.Equal([original], Ids(await this.SearchAsync("4711")));
        var both = await this.SearchAsync("4711", includeEchoes: true);
        Assert.Equal([original, echo], Ids(both));
        Assert.True(both.Hits[1].Score < both.Hits[0].Score);
    }

    [Fact]
    public async Task SearchAsync_TimeOrder_ExhaustivePaginationInTimestampOrder()
    {
        var expected = new List<long>();
        for (var i = 0; i < 25; i++)
        {
            expected.Add(await this.h.SayAsync($"kaffe number {i}", i * 50, channel: i % 2 == 0 ? "a" : "b"));
        }

        await this.IndexAsync();
        expected.Add(await this.h.SayAsync("kaffe late", 5000));

        var seen = new List<long>();
        string? cursor = null;
        HistorySearchResult page;
        do
        {
            page = await this.SearchAsync("kaffe", HistoryOrder.Time, limit: 7, cursor: cursor);
            seen.AddRange(Ids(page));
            cursor = page.HasMore ? HistoryCursorCodec.Encode(page.Binding, page.Next) : null;
        }
        while (cursor is not null);

        Assert.Equal(expected, seen);
        Assert.Null(page.TotalMatches);
    }

    [Fact]
    public async Task SearchAsync_TimeOrder_CompleteSmallResultReportsCount()
    {
        await this.h.SayAsync("plan alpha", 0);
        await this.h.SayAsync("plan beta", 60);
        await this.IndexAsync();

        var result = await this.SearchAsync("plan", HistoryOrder.Time);

        Assert.False(result.HasMore);
        Assert.Equal(2, result.TotalMatches);
    }

    [Fact]
    public async Task SearchAsync_TimeOrder_RequiresAllTerms()
    {
        await this.h.SayAsync("oslo only", 0);
        var both = await this.h.SayAsync("oslo and bergen", 1);
        await this.IndexAsync();

        Assert.Equal([both], Ids(await this.SearchAsync("oslo bergen", HistoryOrder.Time)));
    }

    [Fact]
    public async Task SearchAsync_CursorFromDifferentIndexState_IsRejected()
    {
        for (var i = 0; i < 5; i++)
        {
            await this.h.SayAsync($"budget {i}", i * 60);
        }

        await this.IndexAsync();
        var first = await this.SearchAsync("budget", HistoryOrder.Time, limit: 2);
        var cursor = HistoryCursorCodec.Encode(first.Binding, first.Next);

        await this.h.SayAsync("budget new", 999);
        await this.h.Projector.ApplyPendingAsync(CancellationToken.None);

        var stale = await this.SearchAsync("budget", HistoryOrder.Time, limit: 2, cursor: cursor);
        Assert.NotNull(stale.Error);
        Assert.Contains("stale", stale.Error, StringComparison.Ordinal);
        Assert.NotNull((await this.SearchAsync("budget", HistoryOrder.Time, limit: 2, cursor: "garbage")).Error);
    }

    [Fact]
    public async Task SearchAsync_CrossLanguagePrefix_FindsInflectedRussian()
    {
        var id = await this.h.SayAsync("Мы поедем поездом в Осло", 0);
        await this.IndexAsync();

        Assert.Equal([id], Ids(await this.SearchAsync("поезд* train")));
    }

    [Fact]
    public async Task SearchAsync_NoIndexYet_UsesTailAndReportsIncompleteCoverage()
    {
        var id = await this.h.SayAsync("lonely kayak", 0);

        var result = await this.SearchAsync("kayak");

        Assert.Equal([id], Ids(result));
        Assert.False(result.Coverage.BuildComplete);
        Assert.Null(result.Coverage.Generation);
    }

    [Fact]
    public async Task ReadAsync_AroundMessageId_ResolvesEveryIndexedCategory()
    {
        var ids = new[]
        {
            await this.h.SayAsync("user line", 0),
            await this.h.SayAsync("assistant line", 1, role: "assistant"),
            await this.h.SayAsync("delivered line", 2, role: "assistant", category: MessageCategory.Proactive),
            await this.h.SayAsync("system line", 3, role: "assistant", category: MessageCategory.System),
            await this.h.SayAsync("internal line", 4, category: MessageCategory.Internal),
        };

        foreach (var id in ids)
        {
            var result = await this.recall.ReadAsync(new HistoryReadRequest { AroundMessageId = id, IncludeInternal = true, Window = 1 }, CancellationToken.None);
            Assert.Null(result.Error);
            Assert.Contains(result.Lines, l => l.MessageId == id);
        }

        var hidden = await this.recall.ReadAsync(new HistoryReadRequest { AroundMessageId = ids[4] }, CancellationToken.None);
        Assert.Contains("includeInternal", hidden.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_DeletedAnchor_ReportsNotFound()
    {
        var id = await this.h.SayAsync("soon gone", 0);
        await this.h.Store.DeleteAllMessagesAsync();

        var result = await this.recall.ReadAsync(new HistoryReadRequest { AroundMessageId = id }, CancellationToken.None);

        Assert.Contains("not found", result.Error, StringComparison.Ordinal);
    }
}
