namespace Cortex.Contained.Agent.Host.Evaluation.Calibration;

/// <summary>Disagreement weights of a weighted κ over the ordinal grades 0–2.</summary>
public enum KappaWeights
{
    /// <summary>|i − j| / 2: one step apart costs half of two steps apart.</summary>
    Linear,

    /// <summary>(i − j)² / 4: one step apart costs a quarter of two steps apart.</summary>
    Quadratic,
}

/// <summary>
/// Agreement between the judge and the owner on the 0–2 relevance grades beyond the gate's unweighted Cohen's κ
/// (<see cref="EvalStatistics.CohensKappa"/>): weighted κ, which counts a one-step disagreement less than a two-step
/// one, and the confusion matrix.
/// </summary>
public static class AgreementStatistics
{
    /// <summary>The grades: 0, 1 and 2.</summary>
    public const int Categories = 3;

    /// <summary>
    /// Weighted Cohen's κ = 1 − Σ w·observed / Σ w·expected over the grades 0–2, or null when it is undefined (no items,
    /// or no expected disagreement because both raters gave one and the same grade throughout).
    /// </summary>
    public static double? WeightedKappa(IReadOnlyList<int> judge, IReadOnlyList<int> owner, KappaWeights weights)
    {
        ArgumentNullException.ThrowIfNull(judge);
        var confusion = Confusion(judge, owner);
        var n = (double)judge.Count;
        if (n == 0)
        {
            return null;
        }

        var rows = Enumerable.Range(0, Categories).Select(i => confusion[i].Sum()).ToArray();
        var columns = Enumerable.Range(0, Categories).Select(j => confusion.Sum(r => r[j])).ToArray();
        double observed = 0;
        double expected = 0;
        for (var i = 0; i < Categories; i++)
        {
            for (var j = 0; j < Categories; j++)
            {
                var weight = Weight(i, j, weights);
                observed += weight * confusion[i][j] / n;
                expected += weight * rows[i] * columns[j] / (n * n);
            }
        }

        return expected <= 1e-12 ? null : 1 - (observed / expected);
    }

    /// <summary>Counts per (judge grade, owner grade): rows are the judge, columns the owner.</summary>
    public static int[][] Confusion(IReadOnlyList<int> judge, IReadOnlyList<int> owner)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(owner);
        if (judge.Count != owner.Count)
        {
            throw new ArgumentException("Both raters must grade the same items.", nameof(owner));
        }

        var confusion = Enumerable.Range(0, Categories).Select(_ => new int[Categories]).ToArray();
        for (var k = 0; k < judge.Count; k++)
        {
            if (judge[k] is < 0 or >= Categories || owner[k] is < 0 or >= Categories)
            {
                throw new ArgumentOutOfRangeException(nameof(judge), "Grades are 0, 1 or 2.");
            }

            confusion[judge[k]][owner[k]]++;
        }

        return confusion;
    }

    private static double Weight(int i, int j, KappaWeights weights)
    {
        var distance = Math.Abs(i - j) / (double)(Categories - 1);
        return weights == KappaWeights.Quadratic ? distance * distance : distance;
    }
}
