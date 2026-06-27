using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Agent.Host.Tests;

/// <summary>
/// Tests for <see cref="NightlyTaskScheduler"/>. Verifies the shared scheduling
/// helper used by nightly maintenance services (compaction, etc.).
/// </summary>
public class NightlyTaskSchedulerTests
{
    // ── CalculateDelayToNextTime ────────────────────────────────────────

    [Fact]
    public void CalculateDelay_PreferredTimeInFuture_ReturnsDelayToday()
    {
        // Now is 10:00, preferred is 14:00 → should wait ~4 hours
        var now = new DateTimeOffset(2026, 3, 13, 10, 0, 0, TimeSpan.Zero);
        var delay = NightlyTaskScheduler.CalculateDelayToNextTime("14:00", now);

        Assert.Equal(TimeSpan.FromHours(4), delay);
    }

    [Fact]
    public void CalculateDelay_PreferredTimeAlreadyPassed_ReturnsDelayTomorrow()
    {
        // Now is 15:00, preferred is 02:30 → should wait until tomorrow 02:30
        var now = new DateTimeOffset(2026, 3, 13, 15, 0, 0, TimeSpan.Zero);
        var delay = NightlyTaskScheduler.CalculateDelayToNextTime("02:30", now);

        var expected = TimeSpan.FromHours(11) + TimeSpan.FromMinutes(30);
        Assert.Equal(expected, delay);
    }

    [Fact]
    public void CalculateDelay_InvalidTimeString_Returns24Hours()
    {
        var now = DateTimeOffset.UtcNow;
        var delay = NightlyTaskScheduler.CalculateDelayToNextTime("not-a-time", now);

        Assert.Equal(TimeSpan.FromHours(24), delay);
    }

    [Fact]
    public void CalculateDelay_VeryCloseToPreferredTime_HasMinimumDelay()
    {
        // Now is 02:29:50, preferred is 02:30 → delay would be 10 seconds,
        // but minimum is 1 minute so it should add a day.
        var now = new DateTimeOffset(2026, 3, 13, 2, 29, 50, TimeSpan.Zero);
        var delay = NightlyTaskScheduler.CalculateDelayToNextTime("02:30", now);

        Assert.True(delay >= TimeSpan.FromMinutes(1));
    }

    // ── GetMissedDates ─────────────────────────────────────────────────

    [Fact]
    public void GetMissedDates_NeverRun_ReturnsYesterday()
    {
        var today = new DateOnly(2026, 3, 13);
        var missed = NightlyTaskScheduler.GetMissedDates(lastCompletedDate: null, today);

        Assert.Single(missed);
        Assert.Equal(new DateOnly(2026, 3, 12), missed[0]);
    }

    [Fact]
    public void GetMissedDates_RanYesterday_ReturnsEmpty()
    {
        var today = new DateOnly(2026, 3, 13);
        var lastCompleted = new DateOnly(2026, 3, 12);

        var missed = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        Assert.Empty(missed);
    }

    [Fact]
    public void GetMissedDates_RanToday_ReturnsEmpty()
    {
        var today = new DateOnly(2026, 3, 13);
        var lastCompleted = new DateOnly(2026, 3, 13);

        var missed = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        Assert.Empty(missed);
    }

    [Fact]
    public void GetMissedDates_Missed3Days_Returns3Dates()
    {
        var today = new DateOnly(2026, 3, 13);
        var lastCompleted = new DateOnly(2026, 3, 9); // missed 10, 11, 12

        var missed = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        Assert.Equal(3, missed.Count);
        Assert.Equal(new DateOnly(2026, 3, 10), missed[0]);
        Assert.Equal(new DateOnly(2026, 3, 11), missed[1]);
        Assert.Equal(new DateOnly(2026, 3, 12), missed[2]);
    }

    [Fact]
    public void GetMissedDates_MissedManyDays_CapsAtMax()
    {
        var today = new DateOnly(2026, 3, 13);
        var lastCompleted = new DateOnly(2026, 2, 1); // missed ~40 days

        var missed = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        Assert.Equal(NightlyTaskScheduler.MaxCatchUpRuns, missed.Count);
        // Should return the earliest missed dates (oldest first)
        Assert.Equal(new DateOnly(2026, 2, 2), missed[0]);
        Assert.Equal(new DateOnly(2026, 2, 3), missed[1]);
        Assert.Equal(new DateOnly(2026, 2, 4), missed[2]);
    }

    [Fact]
    public void GetMissedDates_Missed1Day_Returns1Date()
    {
        var today = new DateOnly(2026, 3, 13);
        var lastCompleted = new DateOnly(2026, 3, 11); // missed only 12

        var missed = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        Assert.Single(missed);
        Assert.Equal(new DateOnly(2026, 3, 12), missed[0]);
    }

    [Fact]
    public void GetMissedDates_ReturnsDatesInChronologicalOrder()
    {
        var today = new DateOnly(2026, 3, 13);
        var lastCompleted = new DateOnly(2026, 3, 9);

        var missed = NightlyTaskScheduler.GetMissedDates(lastCompleted, today);

        for (int i = 1; i < missed.Count; i++)
        {
            Assert.True(missed[i] > missed[i - 1], "Dates should be in chronological order");
        }
    }

    // ── DateToTimeWindow ───────────────────────────────────────────────

    [Fact]
    public void DateToTimeWindow_ReturnsFullDayUtcWindow()
    {
        var date = new DateOnly(2026, 3, 12);
        var (after, before) = NightlyTaskScheduler.DateToTimeWindow(date);

        Assert.Equal(new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero), after);
        Assert.Equal(new DateTimeOffset(2026, 3, 13, 0, 0, 0, TimeSpan.Zero), before);
    }

    [Fact]
    public void DateToTimeWindow_SpansExactly24Hours()
    {
        var date = new DateOnly(2026, 3, 12);
        var (after, before) = NightlyTaskScheduler.DateToTimeWindow(date);

        Assert.Equal(TimeSpan.FromHours(24), before - after);
    }
}
