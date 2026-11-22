using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

/// <summary>Keyword quality ablation (§4.2): stemmed shadow column and trigram table, end to end.</summary>
public sealed class HistoryKeywordVariantSearchTests
{
    private static async Task<HistorySearchResult> SearchAsync(HistoryKeywordVariant variant, string query, HistoryOrder order = HistoryOrder.Relevance)
    {
        await using var h = new HistoryHarness(new HistoryOptions { Enabled = true, TimeZone = "UTC", KeywordVariant = variant });
        await h.SayAsync("мы поедем поездом в TownB", 0);
        await h.SayAsync("the invoice INV2041 arrived", 1, channel: "mail");
        await h.Projector.InitializeAsync(CancellationToken.None);
        await h.CatchUpAsync();
        var recall = new HistoryRecallService(h.Store, h.Projector, h.Gate, h.Status, h.Monitor);
        return await recall.SearchAsync(new HistorySearchRequest { Query = query, Order = order }, CancellationToken.None);
    }

    [Fact]
    public async Task Stemmed_InflectedQuery_FindsOtherForm()
    {
        Assert.Empty((await SearchAsync(HistoryKeywordVariant.Plain, "поезда")).Hits);

        var hit = Assert.Single((await SearchAsync(HistoryKeywordVariant.Stemmed, "поезда")).Hits);
        Assert.Contains("поездом", hit.Lines[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stemmed_TimeOrder_KeepsExactWords()
    {
        Assert.Empty((await SearchAsync(HistoryKeywordVariant.Stemmed, "поезда", HistoryOrder.Time)).Hits);
        Assert.Single((await SearchAsync(HistoryKeywordVariant.Stemmed, "поездом", HistoryOrder.Time)).Hits);
    }

    [Fact]
    public async Task Trigram_SubstringOfIdentifier_Matches()
    {
        Assert.Empty((await SearchAsync(HistoryKeywordVariant.Plain, "2041")).Hits);

        var hit = Assert.Single((await SearchAsync(HistoryKeywordVariant.Trigram, "2041")).Hits);
        Assert.Equal("mail", hit.ChannelId);
    }

    [Fact]
    public void ForVariant_BuildsColumnQualifiedExpressions()
    {
        var parsed = QueryParser.Build(QueryParser.Split("rejser \"night train\" tog*"), HistoryOrder.Relevance, _ => 1, 10, 0.2)!;

        var stemmed = QueryParser.ForVariant(parsed, HistoryKeywordVariant.Stemmed, HistoryOrder.Relevance);
        var trigram = QueryParser.ForVariant(parsed, HistoryKeywordVariant.Trigram, HistoryOrder.Relevance);
        var trigramTime = QueryParser.ForVariant(parsed, HistoryKeywordVariant.Trigram, HistoryOrder.Time);

        Assert.Same(parsed, QueryParser.ForVariant(parsed, HistoryKeywordVariant.Plain, HistoryOrder.Relevance));
        Assert.Equal("body : (\"rejser\" OR \"night train\" OR \"tog\"*) OR stem : (\"rejs\" OR \"rejser\")", stemmed.MatchExpression);
        Assert.Equal(HistoryKeywordVariant.Stemmed, stemmed.Table);
        Assert.Equal("\"rejser\" OR \"night train\" OR \"tog\"", trigram.MatchExpression);
        Assert.Equal(HistoryKeywordVariant.Trigram, trigram.Table);
        Assert.Same(parsed, trigramTime);
    }
}
