namespace Cortex.Contained.Agent.Host.Agent;

/// <summary>
/// Thread-safe rolling window of latency samples (last <c>capacity</c> values) that
/// computes average and nearest-rank p50/p95. Used by <see cref="AgentMetrics"/> to
/// surface an at-a-glance "are turns getting faster?" view without storing history.
/// </summary>
public sealed class LatencyStats
{
    private readonly long[] buffer;
    private readonly Lock syncLock = new();
    private int count;
    private int writeIndex;

    public LatencyStats(int capacity = 100)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.buffer = new long[capacity];
    }

    /// <summary>Record a sample (milliseconds). Evicts the oldest when the window is full.</summary>
    public void Add(long sampleMs)
    {
        lock (this.syncLock)
        {
            this.buffer[this.writeIndex] = sampleMs;
            this.writeIndex = (this.writeIndex + 1) % this.buffer.Length;
            if (this.count < this.buffer.Length)
            {
                this.count++;
            }
        }
    }

    /// <summary>Coherent snapshot of the current window's average and p50/p95.</summary>
    public LatencySummary Snapshot()
    {
        long[] samples;
        lock (this.syncLock)
        {
            if (this.count == 0)
            {
                return new LatencySummary(0, 0, 0, 0);
            }

            samples = new long[this.count];
            Array.Copy(this.buffer, samples, this.count);
        }

        Array.Sort(samples);

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s;
        }

        return new LatencySummary(
            Count: samples.Length,
            AvgMs: sum / samples.Length,
            P50Ms: Percentile(samples, 0.50),
            P95Ms: Percentile(samples, 0.95));
    }

    /// <summary>Nearest-rank percentile over a pre-sorted ascending array.</summary>
    private static long Percentile(long[] sorted, double p)
    {
        // rank = ceil(p * n), 1-based; index = rank - 1, clamped to [0, n-1].
        var rank = (int)Math.Ceiling(p * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

/// <summary>Immutable summary of a <see cref="LatencyStats"/> window.</summary>
public readonly record struct LatencySummary(int Count, double AvgMs, long P50Ms, long P95Ms);
