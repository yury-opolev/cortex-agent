using Cortex.Contained.Agent.Host.History;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class HybridFusionTests
{
    private static RankedCandidate[] List(params long[] segIds) =>
        segIds.Select((id, i) => new RankedCandidate(id, 10 - i)).ToArray();

    private static HistoryRankingOptions Options(HistoryFusionMethod method = HistoryFusionMethod.Rrf, int k = 60) =>
        new() { Fusion = method, RrfK = k };

    [Fact]
    public void Fuse_Rrf_ScoresAreReciprocalRanks()
    {
        var fused = HybridFusion.Fuse(List(1, 2), List(2, 3), Options(k: 60), exactIntent: false);

        Assert.Equal([2L, 1L, 3L], fused.Select(f => f.SegId));
        Assert.Equal((1.0 / 62) + (1.0 / 61), fused[0].Score, 12);
        Assert.Equal(new FusedCandidate(1, 1.0 / 61, 1, null), fused[1]);
        Assert.Equal(new FusedCandidate(3, 1.0 / 62, null, 2), fused[2]);
    }

    [Fact]
    public void Fuse_ExactIntent_GivesKeywordListPriority()
    {
        var keyword = List(9, 1);
        var dense = List(2);

        var plain = HybridFusion.Fuse(keyword, dense, Options(), exactIntent: false);
        var exact = HybridFusion.Fuse(keyword, dense, Options(), exactIntent: true);

        Assert.True(plain.ToList().FindIndex(f => f.SegId == 2) < plain.ToList().FindIndex(f => f.SegId == 1));
        Assert.True(exact.ToList().FindIndex(f => f.SegId == 1) < exact.ToList().FindIndex(f => f.SegId == 2));
    }

    [Fact]
    public void Fuse_WeightedRrf_ZeroDenseWeight_KeepsKeywordOrder()
    {
        var options = Options(HistoryFusionMethod.WeightedRrf);
        options.DenseWeight = 0;

        var fused = HybridFusion.Fuse(List(3, 1), List(1, 2), options, exactIntent: false);

        Assert.Equal([3L, 1L], fused.Take(2).Select(f => f.SegId));
        Assert.Equal(0, fused[2].Score);
    }

    [Fact]
    public void Fuse_Convex_MinMaxNormalisedScores()
    {
        var options = Options(HistoryFusionMethod.Convex);
        var keyword = new[] { new RankedCandidate(1, 8), new RankedCandidate(2, 4) };
        var dense = new[] { new RankedCandidate(2, 0.9), new RankedCandidate(3, 0.5) };

        var fused = HybridFusion.Fuse(keyword, dense, options, exactIntent: false);

        Assert.Equal(0.5, fused.Single(f => f.SegId == 1).Score, 12);
        Assert.Equal(0.5, fused.Single(f => f.SegId == 2).Score, 12);
        Assert.Equal(0, fused.Single(f => f.SegId == 3).Score, 12);
        Assert.Equal(1L, fused[0].SegId);
    }

    [Theory]
    [InlineData("\"night train\" TownB", true)]
    [InlineData("invoice INV-2041", true)]
    [InlineData("release v1.2", true)]
    [InlineData("call getUser", true)]
    [InlineData("config foo_bar", true)]
    [InlineData("issue #123", true)]
    [InlineData("well-known train times", false)]
    [InlineData("поезд* rejse*", false)]
    [InlineData("", false)]
    public void HasExactIntent_QuotedOrIdentifierLike(string query, bool expected)
    {
        Assert.Equal(expected, QueryIntent.HasExactIntent(query));
    }
}
