using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class RankingMetricsTests
{
    private static readonly Dictionary<long, int> Grades = new() { [1] = 2, [2] = 1, [3] = 0 };
    private static readonly HashSet<long> Relevant = [1, 2];

    [Fact]
    public void Ndcg_PerfectOrderIsOne_ReversedIsLower()
    {
        Assert.Equal(1, RankingMetrics.NdcgAt([1, 2, 3], Grades, 10), 12);

        var reversed = RankingMetrics.NdcgAt([3, 2, 1], Grades, 10);
        var expected = ((1 / Math.Log2(3)) + (3 / Math.Log2(4))) / (3 + (1 / Math.Log2(3)));
        Assert.Equal(expected, reversed, 12);
        Assert.Equal(0, RankingMetrics.NdcgAt([1], new Dictionary<long, int>(), 10));
    }

    [Fact]
    public void RecallMrrJudged_ComputedOnMessageIds()
    {
        IReadOnlyList<long> ranked = [9, 2, 8, 1];

        Assert.Equal(0.5, RankingMetrics.RecallAt(ranked, Relevant, 2));
        Assert.Equal(0.5, RankingMetrics.ReciprocalRank(ranked, Relevant));
        Assert.Equal(0.25, RankingMetrics.JudgedAt(ranked, new HashSet<long> { 1 }, 10));
        Assert.Equal(0, RankingMetrics.ReciprocalRank([9], Relevant));
    }

    [Fact]
    public void SetF1_EnumerationOverlap()
    {
        Assert.Equal(2.0 * 1 / (2 + 2), RankingMetrics.SetF1([1, 7], Relevant), 12);
        Assert.Equal(1, RankingMetrics.SetF1([], new HashSet<long>()));
        Assert.Equal(0, RankingMetrics.SetF1([7], Relevant));
    }

    [Fact]
    public void RankedMessages_AndLocatorCost_FollowReadingOrder()
    {
        var hits = new[]
        {
            new ReturnedHit([5, 6], [new ReturnedLine(5, 10), new ReturnedLine(6, 4)]),
            new ReturnedHit([1], [new ReturnedLine(5, 10), new ReturnedLine(1, 3)]),
        };

        Assert.Equal([5L, 6L, 1L], RankingMetrics.RankedMessages(hits));
        Assert.Equal(27, RankingMetrics.WordsToFirstRelevant(hits, Relevant));
        Assert.Null(RankingMetrics.WordsToFirstRelevant(hits, new HashSet<long> { 99 }));
    }

    [Fact]
    public void Calibration_PerfectlyCalibratedIsZero()
    {
        var perfect = RankingMetrics.Calibration([(1, true), (0, false)]);
        var overconfident = RankingMetrics.Calibration([(0.95, false), (0.95, false)]);

        Assert.Equal(new CalibrationScore(0, 0), perfect);
        Assert.Equal(0.9025, overconfident.Brier, 6);
        Assert.Equal(0.95, overconfident.ExpectedCalibrationError, 6);
    }
}
