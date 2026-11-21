using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Agent.Host.Reranking;
using Cortex.Contained.Agent.Host.Tests.History;
using Cortex.Contained.Contracts.Hub;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cortex.Contained.Agent.Host.Tests.Reranking;

/// <summary><c>history_search</c> hybrid recall with the rerank stage after fusion (§4.3).</summary>
public sealed class HistoryRerankTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new();
    private readonly FakeReranker reranker = new();
    private readonly RerankerOptions rerankerOptions = new();
    private readonly RerankerAccess access = new();
    private readonly HistoryHarness h;
    private readonly HistoryRecallService recall;

    public HistoryRerankTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
        this.rerankerOptions.History.TopN = 2;
        var monitor = Substitute.For<IOptionsMonitor<RerankerOptions>>();
        monitor.CurrentValue.Returns(this.rerankerOptions);
        this.access.Apply(new RerankerGrant { Enabled = true, ApiKey = "k" });
        var stage = new RerankStage(this.reranker, monitor, this.access, NullLogger<RerankStage>.Instance, this.h.Time);
        this.recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor, this.embedder, this.h.Time, stage);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task SeedAsync()
    {
        await this.h.SayAsync("trip budget: the railway journey costs 40", 0);
        await this.h.SayAsync("trip budget: hotel invoice is paid", 200);
        await this.h.SayAsync("trip budget: coffee with PersonA", 400);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        this.h.Time.Advance(TimeSpan.FromMinutes(3));
        while ((await this.h.Projector.EmbedPendingAsync(CancellationToken.None)).Published > 0)
        {
        }
    }

    private Task<HistorySearchResult> SearchAsync(HistorySearchMode mode = HistorySearchMode.Auto, int limit = 8, string? cursor = null) =>
        this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Mode = mode, Limit = limit, Cursor = cursor }, CancellationToken.None);

    [Fact]
    public async Task Hybrid_RerankOn_ReordersTheHead_WithRawLinesOnly()
    {
        await this.SeedAsync();
        var fused = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Rerank = RerankRequest.Off }, CancellationToken.None);
        var promoted = fused.Hits[1].Lines[0].Text;
        this.reranker.Score = (_, document) => document == promoted ? 1 : 0;

        var result = await this.SearchAsync();

        Assert.Equal(promoted, result.Hits[0].Lines[0].Text);
        Assert.Equal(1, result.Hits[0].RerankScore);
        Assert.StartsWith("on (top 2 reordered", result.Coverage.Rerank, StringComparison.Ordinal);
        Assert.Equal(fused.Hits[2].FirstMsgId, result.Hits[2].FirstMsgId);
        Assert.Null(result.Hits[2].RerankScore);
        Assert.All(this.reranker.Calls[0].Documents, d => Assert.StartsWith("trip budget:", d, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RerankFails_FusedOrderKept_AndCoverageSaysSo()
    {
        await this.SeedAsync();
        var fused = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Rerank = RerankRequest.Off }, CancellationToken.None);
        this.reranker.Failure = new RerankerException("The reranker is unreachable.");

        var result = await this.SearchAsync();

        Assert.Equal(fused.Hits.Select(x => x.FirstMsgId), result.Hits.Select(x => x.FirstMsgId));
        Assert.Equal("failed: The reranker is unreachable; fused order kept", result.Coverage.Rerank);
        var text = HistoryResultRenderer.RenderSearch(result, new HistorySearchRequest { Query = "trip budget" }, new HistoryRenderOptions(TimeZoneInfo.Utc, 1500, 200), "HBtest").Text;
        Assert.Contains("rerank: failed: The reranker is unreachable; fused order kept\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeywordMode_IsNotReranked_AndSaysWhy()
    {
        await this.SeedAsync();

        var result = await this.SearchAsync(HistorySearchMode.Keyword);

        Assert.Empty(this.reranker.Calls);
        Assert.Equal("not used: mode=keyword (reranking follows hybrid fusion)", result.Coverage.Rerank);
    }

    [Fact]
    public async Task BridgeDisabledReranking_NoCall_NoCoverageLine()
    {
        await this.SeedAsync();
        this.access.Apply(new RerankerGrant { Enabled = false });

        var result = await this.SearchAsync();

        Assert.Empty(this.reranker.Calls);
        Assert.Null(result.Coverage.Rerank);
    }

    [Fact]
    public async Task PageAfterTheRerankedHead_KeepsFusedOrderWithoutACall()
    {
        await this.SeedAsync();
        var first = await this.SearchAsync(limit: 2);

        var second = await this.SearchAsync(limit: 2, cursor: HistoryCursorCodec.Encode(first.Binding, first.Next));

        Assert.Single(this.reranker.Calls);
        Assert.Single(second.Hits);
        Assert.Equal("not used: this page starts after the reranked top 2", second.Coverage.Rerank);
    }

    [Fact]
    public async Task PagingAfterARerankedFirstPage_KeepsTheSameOrderEvenWhenTheRerankerLaterFails()
    {
        await this.SeedAsync();
        var fused = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Rerank = RerankRequest.Off }, CancellationToken.None);
        var promoted = fused.Hits[1].Lines[0].Text;
        this.reranker.Score = (_, document) => document == promoted ? 1 : 0;
        var first = await this.SearchAsync(limit: 1);
        this.reranker.Failure = new RerankerException("The reranker is unreachable.");

        var second = await this.SearchAsync(limit: 1, cursor: HistoryCursorCodec.Encode(first.Binding, first.Next));
        var third = await this.SearchAsync(limit: 1, cursor: HistoryCursorCodec.Encode(second.Binding, second.Next));

        Assert.Equal(
            [fused.Hits[1].FirstMsgId, fused.Hits[0].FirstMsgId, fused.Hits[2].FirstMsgId],
            new[] { first.Hits[0].FirstMsgId, second.Hits[0].FirstMsgId, third.Hits[0].FirstMsgId });
        Assert.Single(this.reranker.Calls);
        Assert.False(third.HasMore);
    }

    [Fact]
    public async Task PagingAfterAFailedFirstPage_KeepsTheFusedOrderEvenWhenTheRerankerLaterWorks()
    {
        await this.SeedAsync();
        var fused = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Rerank = RerankRequest.Off }, CancellationToken.None);
        var promoted = fused.Hits[1].Lines[0].Text;
        this.reranker.Failure = new RerankerException("The reranker is unreachable.");
        var first = await this.SearchAsync(limit: 1);
        this.reranker.Failure = null;
        this.reranker.Score = (_, document) => document == promoted ? 1 : 0;

        var second = await this.SearchAsync(limit: 1, cursor: HistoryCursorCodec.Encode(first.Binding, first.Next));

        Assert.Equal(fused.Hits[0].FirstMsgId, first.Hits[0].FirstMsgId);
        Assert.Equal(fused.Hits[1].FirstMsgId, second.Hits[0].FirstMsgId);
        Assert.Single(this.reranker.Calls);
    }

    [Fact]
    public async Task CursorFromAnUnrerankedSearch_IsStaleWhenRerankingApplies()
    {
        await this.SeedAsync();
        var plain = await this.recall.SearchAsync(new HistorySearchRequest { Query = "trip budget", Limit = 1, Rerank = RerankRequest.Off }, CancellationToken.None);

        var reranked = await this.SearchAsync(limit: 1, cursor: HistoryCursorCodec.Encode(plain.Binding, plain.Next));

        Assert.NotNull(reranked.Error);
    }
}
