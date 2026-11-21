namespace Cortex.Contained.Agent.Host.History.Evaluation;

/// <summary>Small, dependency-free statistics for the smoke evaluation (§14.2) and behaviour gate (§14.3).</summary>
public static class EvalStatistics
{
    /// <summary>Wilson score interval for <paramref name="successes"/> out of <paramref name="trials"/>.</summary>
    public static (double Low, double High) WilsonInterval(int successes, int trials, double z = 1.96)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, successes);
        if (trials == 0)
        {
            return (0, 1);
        }

        var p = (double)successes / trials;
        var z2 = z * z;
        var denominator = 1 + (z2 / trials);
        var centre = (p + (z2 / (2 * trials))) / denominator;
        var margin = z * Math.Sqrt((p * (1 - p) / trials) + (z2 / (4.0 * trials * trials))) / denominator;
        return (Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }

    /// <summary>Exact two-sided sign test p-value (ties already removed) for <paramref name="wins"/> vs <paramref name="losses"/>.</summary>
    public static double SignTestTwoSided(int wins, int losses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wins);
        ArgumentOutOfRangeException.ThrowIfNegative(losses);
        var n = wins + losses;
        if (n == 0)
        {
            return 1;
        }

        var k = Math.Min(wins, losses);
        double tail = 0;
        for (var i = 0; i <= k; i++)
        {
            tail += Math.Exp(LogChoose(n, i) - (n * Math.Log(2)));
        }

        return Math.Min(1, 2 * tail);
    }

    /// <summary>The <paramref name="percentile"/> (0–100) of <paramref name="values"/> by nearest rank.</summary>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    /// <summary>
    /// Paired bootstrap of the mean difference <c>b − a</c> (§4.2): resamples query indices with
    /// replacement <paramref name="resamples"/> times (seeded, so reports are reproducible) and returns
    /// the observed mean difference with the percentile interval at <paramref name="confidence"/>.
    /// </summary>
    public static (double Mean, double Low, double High) PairedBootstrap(
        IReadOnlyList<double> a, IReadOnlyList<double> b, int resamples = 10_000, int seed = 20261121, double confidence = 0.95)
    {
        var differences = Differences(a, b);
        if (differences.Length == 0)
        {
            return (0, 0, 0);
        }

        var random = new Random(seed);
        var means = new double[resamples];
        for (var r = 0; r < resamples; r++)
        {
            double sum = 0;
            for (var i = 0; i < differences.Length; i++)
            {
                sum += differences[random.Next(differences.Length)];
            }

            means[r] = sum / differences.Length;
        }

        Array.Sort(means);
        var tail = (1 - confidence) / 2;
        var low = means[(int)Math.Floor(tail * (resamples - 1))];
        var high = means[(int)Math.Ceiling((1 - tail) * (resamples - 1))];
        return (differences.Average(), low, high);
    }

    /// <summary>
    /// Two-sided paired randomisation (sign-flip) test of the mean difference <c>b − a</c>: the share of
    /// random sign assignments whose |mean| is at least the observed one (with the +1 correction).
    /// </summary>
    public static double PairedRandomisationTest(IReadOnlyList<double> a, IReadOnlyList<double> b, int resamples = 10_000, int seed = 20261121)
    {
        var differences = Differences(a, b);
        if (differences.Length == 0)
        {
            return 1;
        }

        var observed = Math.Abs(differences.Average());
        var random = new Random(seed);
        var extreme = 0;
        for (var r = 0; r < resamples; r++)
        {
            double sum = 0;
            foreach (var d in differences)
            {
                sum += random.Next(2) == 0 ? d : -d;
            }

            if (Math.Abs(sum / differences.Length) >= observed - 1e-12)
            {
                extreme++;
            }
        }

        return (extreme + 1.0) / (resamples + 1.0);
    }

    /// <summary>Non-inferiority: the lower bound of the difference (new − baseline) lies above −<paramref name="margin"/>.</summary>
    public static bool NonInferior(double lowerBound, double margin) => lowerBound > -margin;

    /// <summary>Cohen's κ between two raters' categorical labels (e.g. LLM vs human grades; must be ≥ 0.6).</summary>
    public static double CohensKappa(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (first.Count != second.Count)
        {
            throw new ArgumentException("Both raters must label the same items.", nameof(second));
        }

        if (first.Count == 0)
        {
            return 0;
        }

        var n = (double)first.Count;
        var observed = first.Zip(second).Count(p => p.First == p.Second) / n;
        var expected = first.Concat(second).Distinct()
            .Sum(label => first.Count(x => x == label) / n * (second.Count(x => x == label) / n));
        return expected >= 1 ? 1 : (observed - expected) / (1 - expected);
    }

    private static double[] Differences(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
        {
            throw new ArgumentException("Paired samples must have the same length.", nameof(b));
        }

        return a.Zip(b, (x, y) => y - x).ToArray();
    }

    private static double LogChoose(int n, int k)
    {
        double result = 0;
        for (var i = 1; i <= k; i++)
        {
            result += Math.Log(n - k + i) - Math.Log(i);
        }

        return result;
    }
}
