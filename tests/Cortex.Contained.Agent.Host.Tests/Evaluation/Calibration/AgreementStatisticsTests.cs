using Cortex.Contained.Agent.Host.Evaluation.Calibration;

namespace Cortex.Contained.Agent.Host.Tests.Evaluation.Calibration;

public sealed class AgreementStatisticsTests
{
    // Judge rows × owner columns of the failed live gate (κ 0.521): mostly one step apart.
    internal static readonly int[][] Matrix = [[19, 5, 2], [6, 12, 7], [0, 4, 20]];

    internal static (List<int> Judge, List<int> Owner) Pairs(int[][] matrix)
    {
        var judge = new List<int>();
        var owner = new List<int>();
        for (var j = 0; j < 3; j++)
        {
            for (var o = 0; o < 3; o++)
            {
                judge.AddRange(Enumerable.Repeat(j, matrix[j][o]));
                owner.AddRange(Enumerable.Repeat(o, matrix[j][o]));
            }
        }

        return (judge, owner);
    }

    [Fact]
    public void WeightedKappa_KnownMatrix_MatchesTheHandComputedValues()
    {
        var (judge, owner) = Pairs(Matrix);

        Assert.Equal(0.521, EvalStatistics.CohensKappa(judge, owner), 3);
        Assert.Equal(0.618, AgreementStatistics.WeightedKappa(judge, owner, KappaWeights.Linear)!.Value, 3);
        Assert.Equal(0.712, AgreementStatistics.WeightedKappa(judge, owner, KappaWeights.Quadratic)!.Value, 3);
    }

    [Fact]
    public void WeightedKappa_PerfectAgreement_One()
    {
        Assert.Equal(1.0, AgreementStatistics.WeightedKappa([0, 1, 2, 2], [0, 1, 2, 2], KappaWeights.Quadratic)!.Value, 6);
    }

    [Fact]
    public void WeightedKappa_DegenerateOrEmpty_Null()
    {
        Assert.Null(AgreementStatistics.WeightedKappa([2, 2, 2], [2, 2, 2], KappaWeights.Linear));
        Assert.Null(AgreementStatistics.WeightedKappa([], [], KappaWeights.Linear));
    }

    [Fact]
    public void WeightedKappa_InvalidInput_Throws()
    {
        Assert.Throws<ArgumentException>(() => AgreementStatistics.WeightedKappa([0, 1], [0], KappaWeights.Linear));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgreementStatistics.WeightedKappa([0, 3], [0, 1], KappaWeights.Linear));
    }

    [Fact]
    public void Confusion_RowsJudgeColumnsOwner()
    {
        var (judge, owner) = Pairs(Matrix);

        var confusion = AgreementStatistics.Confusion(judge, owner);

        Assert.Equal(Matrix, confusion);
    }
}
