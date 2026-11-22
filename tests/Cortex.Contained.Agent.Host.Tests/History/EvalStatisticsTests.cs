using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class EvalStatisticsTests
{
    [Fact]
    public void WilsonInterval_KnownValues()
    {
        var (low, high) = EvalStatistics.WilsonInterval(0, 30);
        Assert.Equal(0, low, 6);
        Assert.Equal(0.1135, high, 3);

        var (low2, high2) = EvalStatistics.WilsonInterval(27, 30);
        Assert.InRange(low2, 0.73, 0.75);
        Assert.InRange(high2, 0.96, 0.97);
    }

    [Theory]
    [InlineData(10, 0, 0.001953)]
    [InlineData(5, 5, 1.0)]
    [InlineData(8, 2, 0.109375)]
    public void SignTestTwoSided_KnownValues(int wins, int losses, double expected)
    {
        Assert.Equal(expected, EvalStatistics.SignTestTwoSided(wins, losses), 5);
    }

    [Fact]
    public void Percentile_NearestRank()
    {
        Assert.Equal(95, EvalStatistics.Percentile(Enumerable.Range(1, 100).Select(i => (double)i).ToList(), 95));
    }

    [Fact]
    public void Score_StopsAtBudgetAndFindsGold()
    {
        var hits = new[]
        {
            new ReturnedHit([1, 2, 3], [new ReturnedLine(1, 10), new ReturnedLine(2, 10)]),
            new ReturnedHit([7, 8], [new ReturnedLine(8, 10)]),
        };

        var small = ReadingBudgetMetrics.Score(hits, new HashSet<long> { 3, 8 }, 15);
        var large = ReadingBudgetMetrics.Score(hits, new HashSet<long> { 3, 8 }, 30);

        Assert.Equal(new BudgetScore(true, false, null, 10), small);
        Assert.Equal(new BudgetScore(true, true, 3, 30), large);
    }

    [Fact]
    public void Words_CountsOnlyVisiblePrefix()
    {
        Assert.Equal(3, ReadingBudgetMetrics.Words("one two  three"));
        Assert.Equal(100, ReadingBudgetMetrics.Words(string.Join(' ', Enumerable.Repeat("a", 500))));
    }
}
