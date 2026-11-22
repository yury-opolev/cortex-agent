using Cortex.Contained.Agent.Host.History.Evaluation;

namespace Cortex.Contained.Agent.Host.Tests.History;

public class EvalStatisticsPairedTests
{
    [Fact]
    public void PairedBootstrap_ConstantImprovement_TightIntervalAroundIt()
    {
        var a = Enumerable.Repeat(0.4, 50).ToList();
        var b = Enumerable.Repeat(0.5, 50).ToList();

        var (mean, low, high) = EvalStatistics.PairedBootstrap(a, b, resamples: 2000);

        Assert.Equal(0.1, mean, 9);
        Assert.Equal(0.1, low, 9);
        Assert.Equal(0.1, high, 9);
    }

    [Fact]
    public void PairedBootstrap_NoisyNoEffect_IntervalCoversZeroAndIsSeeded()
    {
        var random = new Random(3);
        var a = Enumerable.Range(0, 80).Select(_ => random.NextDouble()).ToList();
        var b = a.Select(x => x + ((random.NextDouble() - 0.5) * 0.2)).ToList();

        var first = EvalStatistics.PairedBootstrap(a, b, resamples: 2000, seed: 1);
        var again = EvalStatistics.PairedBootstrap(a, b, resamples: 2000, seed: 1);

        Assert.True(first.Low < 0 && first.High > 0);
        Assert.Equal(first, again);
    }

    [Fact]
    public void RandomisationTest_ClearEffectSmallP_NoEffectLargeP()
    {
        var a = Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? 0.2 : 0.3).ToList();
        var better = a.Select(x => x + 0.2).ToList();
        var same = a.ToList();

        Assert.True(EvalStatistics.PairedRandomisationTest(a, better, resamples: 2000) < 0.01);
        Assert.Equal(1, EvalStatistics.PairedRandomisationTest(a, same, resamples: 2000));
    }

    [Fact]
    public void PairedBootstrap_TwoPairs_IntervalIsTheRangeOfResampledMeans()
    {
        // differences {0, 1}: resampled means can only be 0, 0.5 or 1; the 2.5% and 97.5% quantiles are the extremes.
        var (mean, low, high) = EvalStatistics.PairedBootstrap([0, 0], [0, 1], resamples: 4000);

        Assert.Equal(0.5, mean, 12);
        Assert.Equal(0, low, 12);
        Assert.Equal(1, high, 12);
    }

    [Fact]
    public void RandomisationTest_TwoEqualDifferences_ExactPIsHalf()
    {
        // differences {1, 1}: the four sign assignments give means 1, 0, 0, -1; |mean| >= 1 in two of four.
        var p = EvalStatistics.PairedRandomisationTest([0, 0], [1, 1], resamples: 20_000);

        Assert.Equal(0.5, p, 2);
    }

    [Fact]
    public void NonInferiority_LowerBoundAgainstMargin()
    {
        Assert.True(EvalStatistics.NonInferior(-0.01, 0.02));
        Assert.False(EvalStatistics.NonInferior(-0.03, 0.02));
    }

    [Fact]
    public void CohensKappa_AgreementBeyondChance()
    {
        Assert.Equal(1, EvalStatistics.CohensKappa([0, 1, 2, 2], [0, 1, 2, 2]), 12);
        Assert.Equal(0.5, EvalStatistics.CohensKappa([1, 1, 0, 0], [1, 0, 0, 0]), 12);
        Assert.Throws<ArgumentException>(() => EvalStatistics.CohensKappa([1], [1, 2]));
    }
}
