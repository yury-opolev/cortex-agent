using Cortex.Contained.Agent.Host.History;
using Cortex.Contained.Contracts.Hub;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary><c>history_search</c> hybrid mode: fusion, verification, fallback and coverage (§4.2).</summary>
public sealed class HistoryHybridSearchTests : IAsyncDisposable
{
    private readonly FakeHistoryEmbedder embedder = new(new Dictionary<string, string>
    {
        ["train"] = "railway",
        ["trip"] = "journey",
    });

    private readonly HistoryHarness h;
    private readonly HistoryRecallService recall;

    public HistoryHybridSearchTests()
    {
        var options = new HistoryOptions { Enabled = true, TimeZone = "UTC" };
        options.Dense.Enabled = true;
        this.h = new HistoryHarness(options, this.embedder);
        this.recall = new HistoryRecallService(this.h.Store, this.h.Projector, this.h.Gate, this.h.Status, this.h.Monitor, this.embedder, this.h.Time);
    }

    public ValueTask DisposeAsync() => this.h.DisposeAsync();

    private async Task<long> SeedAsync()
    {
        var railway = await this.h.SayAsync("we booked the railway journey to TownB for Friday", 0);
        await this.h.SayAsync("coffee with PersonA about the budget", 200);
        await this.h.SayAsync("invoice INV2041 is paid", 400);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        return railway;
    }

    private async Task EmbedAllAsync()
    {
        this.h.Time.Advance(TimeSpan.FromMinutes(3));
        while ((await this.h.Projector.EmbedPendingAsync(CancellationToken.None)).Published > 0)
        {
        }
    }

    private Task<HistorySearchResult> SearchAsync(string query, HistorySearchMode mode = HistorySearchMode.Auto, int limit = 8, string? cursor = null, bool includeInternal = false) =>
        this.recall.SearchAsync(new HistorySearchRequest { Query = query, Mode = mode, Limit = limit, Cursor = cursor, IncludeInternal = includeInternal }, CancellationToken.None);

    [Fact]
    public async Task Auto_ParaphraseWithoutSharedWords_FoundSemantically()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();

        var keyword = await this.SearchAsync("train trip", HistorySearchMode.Keyword);
        var hybrid = await this.SearchAsync("train trip");

        Assert.Empty(keyword.Hits);
        Assert.Equal(HistorySearchMode.Hybrid, hybrid.Mode);
        Assert.Equal("on", hybrid.Coverage.Dense);
        var top = hybrid.Hits[0];
        Assert.Equal(HistoryMatchKind.Semantic, top.MatchedBy);
        Assert.Contains("railway journey", top.Lines[0].Text, StringComparison.Ordinal);
        Assert.Equal("train trip", this.embedder.Queries[^1]);
    }

    [Fact]
    public async Task Auto_DefaultSearchModeKeyword_StaysKeywordWithoutEmbeddingTheQuery()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();
        this.h.Options.DefaultSearchMode = HistorySearchMode.Keyword;

        var result = await this.SearchAsync("railway");

        Assert.Equal(HistorySearchMode.Keyword, result.Mode);
        Assert.Equal("not used: mode=keyword (default)", result.Coverage.Dense);
        Assert.Empty(this.embedder.Queries);
    }

    [Fact]
    public async Task Hybrid_DefaultSearchModeKeyword_ExplicitHybridStillHybrid()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();
        this.h.Options.DefaultSearchMode = HistorySearchMode.Keyword;

        var result = await this.SearchAsync("train trip", HistorySearchMode.Hybrid);

        Assert.Equal(HistorySearchMode.Hybrid, result.Mode);
        Assert.Contains("railway journey", result.Hits[0].Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hybrid_KeywordAndDenseAgree_MatchedByBothRanksFirst()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();

        var result = await this.SearchAsync("railway journey");

        Assert.Equal(HistoryMatchKind.Both, result.Hits[0].MatchedBy);
        Assert.Contains("TownB", result.Hits[0].Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedderFails_FallsBackToKeywordAndSaysSo()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();
        this.embedder.Failure = new InvalidOperationException("down");

        var result = await this.SearchAsync("invoice");

        Assert.Equal(HistorySearchMode.Keyword, result.Mode);
        Assert.Equal("fallback: the embedder failed", result.Coverage.Dense);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task NothingEmbeddedYet_KeywordOnlyWithLagInCoverage()
    {
        await this.SeedAsync();

        var result = await this.SearchAsync("invoice");

        Assert.Equal(HistorySearchMode.Keyword, result.Mode);
        Assert.Equal("fallback: no chunk is embedded yet", result.Coverage.Dense);
        Assert.Equal(3, result.Coverage.Chunks!.Pending);
    }

    [Fact]
    public async Task EmbedderNotAllowed_ReportsUnavailable()
    {
        await this.SeedAsync();
        this.embedder.UnavailableReason = "endpoint not allowed";

        var result = await this.SearchAsync("invoice");

        Assert.Equal("unavailable: endpoint not allowed", result.Coverage.Dense);
        Assert.Empty(this.embedder.Queries);
    }

    [Fact]
    public async Task QueryVectorDimensionsDiffer_FallsBackToKeywordWithReason()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();
        this.embedder.QueryDimensions = FakeHistoryEmbedder.Dimensions / 2;

        var result = await this.SearchAsync("invoice");

        Assert.Equal(HistorySearchMode.Keyword, result.Mode);
        Assert.Contains("dimensions", result.Coverage.Dense, StringComparison.Ordinal);
        Assert.StartsWith("fallback:", result.Coverage.Dense, StringComparison.Ordinal);
        Assert.Single(result.Hits);
    }

    [Fact]
    public async Task SourceEditedAfterEmbedding_StaleVectorNeverServed()
    {
        var railway = await this.SeedAsync();
        await this.EmbedAllAsync();
        await this.h.Store.UpdateContentAsync(railway, "we booked the ferry voyage to TownB for Friday");

        var result = await this.SearchAsync("train trip");

        Assert.DoesNotContain(result.Hits, hit => hit.MemberIds.Contains(railway));
    }

    [Fact]
    public async Task InternalOnlyChunk_HiddenUnlessIncludeInternal()
    {
        await this.h.SayAsync("railway journey reminder for the task", 0, role: "assistant", category: MessageCategory.Internal);
        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.EmbedAllAsync();

        Assert.Empty((await this.SearchAsync("train trip")).Hits);
        Assert.Single((await this.SearchAsync("train trip", includeInternal: true)).Hits);
    }

    [Fact]
    public async Task Pagination_CursorContinuesFusedList()
    {
        await this.SeedAsync();
        await this.EmbedAllAsync();

        var first = await this.SearchAsync("budget invoice journey", limit: 1);
        var cursor = HistoryCursorCodec.Encode(first.Binding, first.Next);
        var second = await this.SearchAsync("budget invoice journey", limit: 1, cursor: cursor);

        Assert.True(first.HasMore);
        Assert.NotEqual(first.Hits[0].FirstMsgId, second.Hits[0].FirstMsgId);
        Assert.Null(second.Error);
    }

    [Fact]
    public async Task NearDuplicateWorkerBriefs_CollapsedToOne()
    {
        for (var day = 0; day < 3; day++)
        {
            await this.h.SayAsync("daily brief: weather sunny, three meetings, railway strike", day * 1440, role: "assistant", category: MessageCategory.Proactive);
        }

        await this.h.Projector.InitializeAsync(CancellationToken.None);
        await this.h.CatchUpAsync();
        await this.EmbedAllAsync();

        var result = await this.SearchAsync("daily brief");

        Assert.Single(result.Hits);
        Assert.Equal(2, result.CollapsedDuplicates);
    }
}
