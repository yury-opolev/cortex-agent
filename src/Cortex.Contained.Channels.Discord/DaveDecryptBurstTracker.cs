using System.Collections.Concurrent;

namespace Cortex.Contained.Channels.Discord;

/// <summary>Summary of a finished decrypt-failure run, for the diagnostic log.</summary>
public readonly record struct DaveBurstSummary(ulong UserId, long FailureCount, long DurationMs, string ResultCode);

/// <summary>Snapshot of the currently-worst active run, for the recovery watchdog.</summary>
public readonly record struct DaveBurstProbe(long FailuresSinceReset, long TicksSinceFirstFailure);

/// <summary>
/// Per-user accumulator for inbound DAVE decrypt failures. A "run" is an
/// uninterrupted sequence of failures for one user, ended by a successful speech
/// commit (<see cref="Reset(ulong, long)"/>) or a (re)join
/// (<see cref="ResetAll(long)"/>). Feeds both the diagnostic burst-summary log
/// and <see cref="DaveDecryptFloodPolicy"/>. Thread-safe (per-user lock via the
/// concurrent dictionary; counters are only touched under the entry lock).
/// </summary>
public sealed class DaveDecryptBurstTracker
{
    private sealed class Run
    {
        public long Count;
        public long FirstFailureTicks;
        public long LastFailureTicks;
        public string ResultCode = "Unknown";
    }

    private readonly ConcurrentDictionary<ulong, Run> runs = new();

    /// <summary>Records a decrypt failure for <paramref name="userId"/>, starting a new run if none is currently active.</summary>
    public void RecordFailure(ulong userId, string resultCode, long nowTicks)
    {
        var run = this.runs.GetOrAdd(userId, _ => new Run());
        lock (run)
        {
            if (run.Count == 0)
            {
                run.FirstFailureTicks = nowTicks;
            }

            run.Count++;
            run.LastFailureTicks = nowTicks;
            run.ResultCode = resultCode;
        }
    }

    /// <summary>Ends the active run for <paramref name="userId"/> and returns a summary, or <see langword="null"/> if no run was active.</summary>
    public DaveBurstSummary? Reset(ulong userId, long nowTicks)
    {
        if (!this.runs.TryGetValue(userId, out var run))
        {
            return null;
        }

        lock (run)
        {
            if (run.Count == 0)
            {
                return null;
            }

            var summary = new DaveBurstSummary(
                userId,
                run.Count,
                (run.LastFailureTicks - run.FirstFailureTicks) / TimeSpan.TicksPerMillisecond,
                run.ResultCode);

            run.Count = 0;
            run.FirstFailureTicks = 0;
            run.LastFailureTicks = 0;
            return summary;
        }
    }

    /// <summary>Ends every active run (e.g. on a (re)join) and returns a summary for each one that was active.</summary>
    public IReadOnlyList<DaveBurstSummary> ResetAll(long nowTicks)
    {
        var summaries = new List<DaveBurstSummary>();
        foreach (var userId in this.runs.Keys)
        {
            var summary = this.Reset(userId, nowTicks);
            if (summary is not null)
            {
                summaries.Add(summary.Value);
            }
        }

        return summaries;
    }

    /// <summary>Returns a snapshot of the currently-worst active run (highest accumulated failure count) across all users, or a zeroed probe if none is active.</summary>
    public DaveBurstProbe WorstActive(long nowTicks)
    {
        long worstCount = 0;
        long worstFirstTicks = nowTicks;
        foreach (var run in this.runs.Values)
        {
            lock (run)
            {
                if (run.Count > worstCount)
                {
                    worstCount = run.Count;
                    worstFirstTicks = run.FirstFailureTicks;
                }
            }
        }

        return new DaveBurstProbe(worstCount, worstCount == 0 ? 0 : nowTicks - worstFirstTicks);
    }
}
