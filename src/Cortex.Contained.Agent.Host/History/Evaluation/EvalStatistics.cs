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
