using System.Globalization;

namespace Cortex.Contained.Agent.Host.Memory;

/// <summary>
/// Shared scheduling helper for nightly maintenance tasks (compaction, etc.).
/// Encapsulates the "calculate delay to preferred time" and "detect missed days" logic
/// so that individual services don't duplicate it.
/// </summary>
internal static class NightlyTaskScheduler
{
    /// <summary>
    /// Maximum number of catch-up runs to perform per startup when days have been missed.
    /// Prevents runaway execution after long periods of downtime.
    /// </summary>
    public const int MaxCatchUpRuns = 3;

    /// <summary>
    /// Minimum delay before starting catch-up runs after startup,
    /// to allow the system to stabilize.
    /// </summary>
    public static readonly TimeSpan CatchUpStartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Calculates the delay from <paramref name="now"/> until the next occurrence
    /// of <paramref name="preferredTime"/>. If the preferred time has already passed
    /// today, it schedules for tomorrow.
    /// </summary>
    /// <param name="preferredTime">Time of day in "HH:mm" format.</param>
    /// <param name="now">Current time.</param>
    /// <returns>Delay until the next occurrence, or 24 hours if the time string is invalid.</returns>
    public static TimeSpan CalculateDelayToNextTime(string preferredTime, DateTimeOffset now)
    {
        if (!TimeOnly.TryParse(preferredTime, CultureInfo.InvariantCulture, out var time))
        {
            return TimeSpan.FromHours(24);
        }

        var todayAtPreferred = now.Date + time.ToTimeSpan();

        var target = todayAtPreferred <= now.DateTime
            ? todayAtPreferred.AddDays(1)
            : todayAtPreferred;

        var delay = target - now.DateTime;

        // Minimum 1 minute delay to avoid tight loops from timing edge cases
        if (delay < TimeSpan.FromMinutes(1))
        {
            delay = delay.Add(TimeSpan.FromDays(1));
        }

        return delay;
    }

    /// <summary>
    /// Determines which dates need catch-up runs based on the last completed date
    /// and the current date. Returns the dates in chronological order (oldest first),
    /// capped at <see cref="MaxCatchUpRuns"/>.
    /// </summary>
    /// <param name="lastCompletedDate">
    /// The last date that was successfully processed, or <c>null</c> if the task has never run.
    /// When <c>null</c>, only yesterday is returned (we don't try to catch up from the dawn of time).
    /// </param>
    /// <param name="today">Today's date (the caller should use local date, matching the preferred time zone).</param>
    /// <returns>
    /// A list of dates to process. Empty if no catch-up is needed (last run was yesterday or today).
    /// </returns>
    public static List<DateOnly> GetMissedDates(DateOnly? lastCompletedDate, DateOnly today)
    {
        // Nightly tasks handle "yesterday's" data (the day that ended before the nightly run).
        // So the most recent date to catch up is yesterday.
        var yesterday = today.AddDays(-1);

        if (lastCompletedDate is null)
        {
            // Never run before — just handle yesterday so we don't try to
            // process the entire history on first deployment.
            return [yesterday];
        }

        if (lastCompletedDate.Value >= yesterday)
        {
            // Already up to date — nothing to catch up.
            return [];
        }

        // Compute missed dates: day after last completed through yesterday.
        var missed = new List<DateOnly>();
        var date = lastCompletedDate.Value.AddDays(1);

        while (date <= yesterday && missed.Count < MaxCatchUpRuns)
        {
            missed.Add(date);
            date = date.AddDays(1);
        }

        return missed;
    }

    /// <summary>
    /// Converts a <see cref="DateOnly"/> to a UTC time window for fetching
    /// conversations. The window covers the full 24-hour day.
    /// </summary>
    public static (DateTimeOffset After, DateTimeOffset Before) DateToTimeWindow(DateOnly date)
    {
        var after = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var before = after.AddDays(1);
        return (after, before);
    }
}
