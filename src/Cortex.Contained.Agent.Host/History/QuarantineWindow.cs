namespace Cortex.Contained.Agent.Host.History;

/// <summary>A half-open time window <c>[From, To)</c> whose curated memories are quarantined (D10).</summary>
public sealed record QuarantineWindow(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>True when <paramref name="instant"/> falls inside the window.</summary>
    public bool Contains(DateTimeOffset instant) => instant >= this.From && instant < this.To;
}

/// <summary>A stored quarantine window with the purge that recorded it.</summary>
/// <param name="ErasureId">Erasure workflow id of the purge.</param>
/// <param name="Window">The window.</param>
/// <param name="PurgedAt">When the purge ran.</param>
/// <param name="ChannelId">The purged channel, or null for an all-channel purge (and windows recorded before this column existed).</param>
public sealed record QuarantineWindowRecord(string ErasureId, QuarantineWindow Window, DateTimeOffset PurgedAt, string? ChannelId);

/// <summary>
/// Turns the timestamps of purged rows into quarantine windows: rows closer than the cluster gap
/// share a window; each window ends <c>slack</c> after its last row (memory extraction runs at
/// compaction, after the conversation), capped at the purge time.
/// </summary>
public static class QuarantineWindowBuilder
{
    /// <summary>Builds windows from ascending <paramref name="sortedTimestamps"/>.</summary>
    public static IReadOnlyList<QuarantineWindow> Build(
        IReadOnlyList<DateTimeOffset> sortedTimestamps,
        TimeSpan clusterGap,
        TimeSpan slack,
        DateTimeOffset cap)
    {
        ArgumentNullException.ThrowIfNull(sortedTimestamps);
        var windows = new List<QuarantineWindow>();
        if (sortedTimestamps.Count == 0)
        {
            return windows;
        }

        var start = sortedTimestamps[0];
        var last = start;
        foreach (var ts in sortedTimestamps.Skip(1))
        {
            if (ts - last > clusterGap)
            {
                windows.Add(Close(start, last, slack, cap));
                start = ts;
            }

            last = ts;
        }

        windows.Add(Close(start, last, slack, cap));
        return windows;
    }

    private static QuarantineWindow Close(DateTimeOffset start, DateTimeOffset last, TimeSpan slack, DateTimeOffset cap)
    {
        var end = last + slack < cap ? last + slack : cap;
        return new QuarantineWindow(start, end > last ? end : last.AddTicks(1));
    }
}
