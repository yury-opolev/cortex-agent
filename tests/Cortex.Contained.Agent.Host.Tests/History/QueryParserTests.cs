using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class QueryParserTests
{
    private static ParsedHistoryQuery? Build(string raw, HistoryOrder order, Func<string, long>? df = null, long total = 100) =>
        QueryParser.Build(QueryParser.Split(raw), order, df ?? (_ => 1), total, 0.2);

    [Fact]
    public void Build_Relevance_OrJoinsQuotedTokens()
    {
        Assert.Equal("\"oslo\" OR \"tog\"", Build("Oslo tog", HistoryOrder.Relevance)!.MatchExpression);
    }

    [Fact]
    public void Build_Time_AndJoins()
    {
        Assert.Equal("\"oslo\" AND \"tog\"", Build("Oslo tog", HistoryOrder.Time)!.MatchExpression);
    }

    [Fact]
    public void Build_QuotedPhrase_StaysPhrase()
    {
        Assert.Equal("\"night train\" OR \"oslo\"", Build("\"Night Train\" oslo", HistoryOrder.Relevance)!.MatchExpression);
    }

    [Fact]
    public void Build_Prefix_KeepsStar()
    {
        Assert.Equal("\"поезд\"*", Build("Поезд*", HistoryOrder.Relevance)!.MatchExpression);
    }

    [Fact]
    public void Build_ShortPrefix_BecomesExactWord()
    {
        Assert.Equal("\"ab\"", Build("ab*", HistoryOrder.Relevance)!.MatchExpression);
    }

    [Fact]
    public void Build_FtsSyntaxInInput_IsEscaped()
    {
        var query = Build("NEAR(a b) OR col:x AND -y ^z", HistoryOrder.Time)!;

        Assert.Equal("\"near a\" AND \"b\" AND \"or\" AND \"col x\" AND \"and\" AND \"y\" AND \"z\"", query.MatchExpression);
    }

    [Fact]
    public void Build_UnbalancedQuote_IsIgnored()
    {
        Assert.Equal("\"oslo\" OR \"tog\"", Build("oslo \"tog", HistoryOrder.Relevance)!.MatchExpression);
    }

    [Fact]
    public void Build_FoldsDanishAndRussian()
    {
        Assert.Equal("\"sondag\" OR \"елка\"", Build("Søndag Ёлка", HistoryOrder.Relevance)!.MatchExpression);
    }

    [Fact]
    public void Build_Relevance_DropsFrequentTerms()
    {
        var query = Build("the oslo", HistoryOrder.Relevance, df: t => t == "the" ? 90 : 3)!;

        Assert.Equal("\"oslo\"", query.MatchExpression);
        Assert.Single(query.DroppedTerms);
    }

    [Fact]
    public void Build_Relevance_AllFrequent_KeepsRarest()
    {
        var query = Build("the and", HistoryOrder.Relevance, df: t => t == "the" ? 90 : 40)!;

        Assert.Equal("\"and\"", query.MatchExpression);
    }

    [Fact]
    public void Build_Relevance_OnlyUnseenTermsLeft_KeepsRarestFrequentTerm()
    {
        var query = Build("the zyzzyva", HistoryOrder.Relevance, df: t => t == "the" ? 90 : 0)!;

        Assert.Equal("\"zyzzyva\" OR \"the\"", query.MatchExpression);
    }

    [Fact]
    public void Build_Relevance_SmallIndex_NoDfCap()
    {
        var query = Build("the oslo", HistoryOrder.Relevance, df: _ => 1, total: 2)!;

        Assert.Equal("\"the\" OR \"oslo\"", query.MatchExpression);
    }

    [Fact]
    public void Build_Time_NeverDropsFrequentTerms()
    {
        var query = Build("the oslo", HistoryOrder.Time, df: t => t == "the" ? 90 : 3)!;

        Assert.Equal("\"the\" AND \"oslo\"", query.MatchExpression);
    }

    [Fact]
    public void Build_OnlyPunctuation_ReturnsNull()
    {
        Assert.Null(Build("?? !! --", HistoryOrder.Relevance));
    }

    [Fact]
    public void Split_DuplicateTerms_AreDeduplicated()
    {
        Assert.Single(QueryParser.Split("Oslo oslo OSLO"));
    }
}
